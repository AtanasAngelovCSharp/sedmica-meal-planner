// Receives Stripe billing events and keeps the `subscriptions` table in
// sync — the only writer of that table (see schema_and_seed.sql section 4d).
//
// Deployed with --no-verify-jwt: Stripe doesn't send a Supabase access
// token, so the platform's default JWT gate would reject every request.
// Trust instead comes from verifying the Stripe-Signature header against
// STRIPE_WEBHOOK_SIGNING_SECRET (set once the webhook endpoint exists in
// the Stripe dashboard and Stripe hands us its signing secret) before
// touching anything in the payload.

const SUPABASE_URL = Deno.env.get("SUPABASE_URL")!;
const SERVICE_ROLE_KEY = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!;
const STRIPE_WEBHOOK_SIGNING_SECRET = Deno.env.get("STRIPE_WEBHOOK_SIGNING_SECRET")!;

Deno.serve(async (req: Request) => {
  const signature = req.headers.get("Stripe-Signature");
  const body = await req.text();
  if (!signature) return new Response("Missing signature", { status: 400 });

  let event;
  try {
    event = await verifyStripeSignature(body, signature, STRIPE_WEBHOOK_SIGNING_SECRET);
  } catch (err) {
    console.error("Signature verification failed", err);
    return new Response("Invalid signature", { status: 400 });
  }

  try {
    const obj = event.data.object;
    switch (event.type) {
      case "checkout.session.completed": {
        const householdId = obj.metadata?.household_id;
        if (householdId) {
          await upsertSubscription(householdId, {
            stripe_customer_id: obj.customer,
            stripe_subscription_id: obj.subscription,
          });
        }
        break;
      }
      // Covers both the initial subscription (right after checkout) and
      // every later renewal/plan change — Stripe's `current_period_end`
      // and `status` are the source of truth for what HasAccess checks.
      case "customer.subscription.created":
      case "customer.subscription.updated": {
        const householdId = obj.metadata?.household_id;
        if (householdId) {
          await upsertSubscription(householdId, {
            stripe_customer_id: obj.customer,
            stripe_subscription_id: obj.id,
            status: mapStripeStatus(obj.status),
            current_period_end: new Date(obj.current_period_end * 1000).toISOString(),
          });
        }
        break;
      }
      case "customer.subscription.deleted": {
        const householdId = obj.metadata?.household_id;
        if (householdId) {
          await upsertSubscription(householdId, { status: "canceled" });
        }
        break;
      }
    }
  } catch (err) {
    console.error("Webhook handling error", err);
    return new Response("Handler error", { status: 500 });
  }

  return new Response(JSON.stringify({ received: true }), { headers: { "Content-Type": "application/json" } });
});

// Stripe has more statuses than our own trial/active/past_due/canceled —
// collapse the ones that shouldn't affect access (e.g. incomplete_expired)
// into the closest match.
function mapStripeStatus(stripeStatus: string): string {
  if (stripeStatus === "active" || stripeStatus === "trialing") return "active";
  if (stripeStatus === "past_due" || stripeStatus === "unpaid") return "past_due";
  return "canceled";
}

async function upsertSubscription(householdId: string, fields: Record<string, unknown>) {
  const res = await fetch(`${SUPABASE_URL}/rest/v1/subscriptions?household_id=eq.${householdId}`, {
    method: "PATCH",
    headers: {
      apikey: SERVICE_ROLE_KEY,
      Authorization: `Bearer ${SERVICE_ROLE_KEY}`,
      "Content-Type": "application/json",
      Prefer: "return=minimal",
    },
    body: JSON.stringify(fields),
  });
  if (!res.ok) {
    console.error("Failed to update subscription", await res.text());
  }
}

// Stripe's webhook signature scheme: HMAC-SHA256 over "{timestamp}.{body}",
// hex-encoded, sent as "t=...,v1=...". Implemented directly against Web
// Crypto (available in Deno's Edge Function runtime) rather than pulling in
// Stripe's Node SDK, matching the rest of this project's Edge Functions,
// which all talk to external APIs via plain fetch.
async function verifyStripeSignature(payload: string, header: string, secret: string) {
  const parts = Object.fromEntries(header.split(",").map((p) => p.split("=")));
  const timestamp = parts["t"];
  const signature = parts["v1"];
  if (!timestamp || !signature) throw new Error("Malformed Stripe-Signature header");

  const key = await crypto.subtle.importKey(
    "raw",
    new TextEncoder().encode(secret),
    { name: "HMAC", hash: "SHA-256" },
    false,
    ["sign"],
  );
  const sigBytes = await crypto.subtle.sign("HMAC", key, new TextEncoder().encode(`${timestamp}.${payload}`));
  const expected = Array.from(new Uint8Array(sigBytes)).map((b) => b.toString(16).padStart(2, "0")).join("");
  if (expected !== signature) throw new Error("Signature mismatch");

  // Reject stale/replayed deliveries — Stripe recommends a 5 minute window.
  const ageSeconds = Math.abs(Date.now() / 1000 - Number(timestamp));
  if (ageSeconds > 300) throw new Error("Timestamp too old");

  return JSON.parse(payload);
}
