// Creates a Stripe Checkout Session for the calling household's €4.99/month
// subscription and returns its URL for the client to redirect to.
//
// Requires two function secrets that only exist once Stripe is set up
// (`supabase secrets set STRIPE_SECRET_KEY=... STRIPE_PRICE_ID=...`) — the
// client never sees the Stripe secret key. Supabase's default verify-jwt
// gate on Edge Functions ensures only a logged-in user can call this at
// all; we still resolve *their own* household_id server-side (via GoTrue +
// an RLS-scoped household_members read) rather than trusting anything the
// client sends, so nobody can start a checkout for someone else's household.

const SUPABASE_URL = Deno.env.get("SUPABASE_URL")!;
const STRIPE_SECRET_KEY = Deno.env.get("STRIPE_SECRET_KEY")!;
const STRIPE_PRICE_ID = Deno.env.get("STRIPE_PRICE_ID")!;

// Called directly from the browser (the web build), so it needs to answer
// the CORS preflight (OPTIONS) itself and echo these headers on every
// response — without them the browser never even sends the real request,
// it just fails with a generic "Failed to fetch".
const CORS_HEADERS = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, apikey, content-type",
  "Access-Control-Allow-Methods": "POST, OPTIONS",
};

Deno.serve(async (req: Request) => {
  if (req.method === "OPTIONS") {
    return new Response("ok", { headers: CORS_HEADERS });
  }
  if (req.method !== "POST") {
    return json({ ok: false, error: "Method not allowed" }, 405);
  }

  try {
    const apikey = req.headers.get("apikey") ?? "";
    const authHeader = req.headers.get("Authorization") ?? "";
    const { success_url, cancel_url } = await req.json();
    if (!success_url || !cancel_url) {
      return json({ ok: false, error: "Missing success_url/cancel_url." }, 400);
    }

    // The client always builds these from its own Nav.BaseUri (see
    // PaywallScreen.razor), so they should always match the Origin this
    // request actually came from. Rejecting a mismatch stops a forged
    // request (made with a stolen/replayed token) from redirecting a real
    // Stripe checkout to an attacker-controlled domain.
    const requestOrigin = req.headers.get("Origin");
    try {
      const successOrigin = new URL(success_url).origin;
      const cancelOrigin = new URL(cancel_url).origin;
      if (
        successOrigin !== cancelOrigin ||
        (requestOrigin && successOrigin !== requestOrigin)
      ) {
        return json({ ok: false, error: "Invalid success_url/cancel_url." }, 400);
      }
    } catch {
      return json({ ok: false, error: "Invalid success_url/cancel_url." }, 400);
    }

    const userRes = await fetch(`${SUPABASE_URL}/auth/v1/user`, {
      headers: { apikey, Authorization: authHeader },
    });
    if (!userRes.ok) return json({ ok: false, error: "Не си влязъл." }, 401);
    const user = await userRes.json();

    const memberRes = await fetch(
      `${SUPABASE_URL}/rest/v1/household_members?select=household_id&user_id=eq.${user.id}&limit=1`,
      { headers: { apikey, Authorization: authHeader } },
    );
    const members = await memberRes.json();
    if (!Array.isArray(members) || members.length === 0) {
      return json({ ok: false, error: "Нямаш домакинство още." }, 400);
    }
    const householdId = members[0].household_id;

    // Reuse this household's existing Stripe customer if it already has one
    // (e.g. resubscribing after a cancellation), so billing history stays
    // on one customer record instead of fragmenting across several.
    const subRes = await fetch(
      `${SUPABASE_URL}/rest/v1/subscriptions?select=stripe_customer_id&household_id=eq.${householdId}&limit=1`,
      { headers: { apikey, Authorization: authHeader } },
    );
    const subs = await subRes.json();
    const existingCustomerId = Array.isArray(subs) && subs.length > 0 ? subs[0].stripe_customer_id : null;

    const params = new URLSearchParams({
      mode: "subscription",
      "line_items[0][price]": STRIPE_PRICE_ID,
      "line_items[0][quantity]": "1",
      success_url,
      cancel_url,
      "metadata[household_id]": String(householdId),
      "subscription_data[metadata][household_id]": String(householdId),
    });
    if (existingCustomerId) {
      params.set("customer", existingCustomerId);
    } else if (user.email) {
      params.set("customer_email", user.email);
    }

    const stripeRes = await fetch("https://api.stripe.com/v1/checkout/sessions", {
      method: "POST",
      headers: {
        Authorization: `Bearer ${STRIPE_SECRET_KEY}`,
        "Content-Type": "application/x-www-form-urlencoded",
      },
      body: params,
    });
    const session = await stripeRes.json();
    if (!stripeRes.ok) {
      console.error(session);
      return json({ ok: false, error: session.error?.message ?? "Грешка от Stripe." }, 500);
    }

    return json({ ok: true, url: session.url });
  } catch (err) {
    console.error(err);
    return json({ ok: false, error: String(err) }, 500);
  }
});

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json", ...CORS_HEADERS },
  });
}
