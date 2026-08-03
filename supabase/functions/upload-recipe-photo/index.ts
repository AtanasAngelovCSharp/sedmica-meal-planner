// Uploads a recipe photo to the public "recipe-photos" Storage bucket.
//
// storage.objects is owned by Supabase's internal storage role, so this
// project's Management API token can't grant client-side RLS insert
// policies on it directly — the client can't write to Storage on its own.
// This function does the write server-side with the service-role key
// instead (bypassing storage RLS entirely), while Supabase's default
// verify-jwt gate on Edge Functions still ensures only a logged-in user
// (valid access token) can call it at all.

const SUPABASE_URL = Deno.env.get("SUPABASE_URL")!;
const SERVICE_ROLE_KEY = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!;
const BUCKET = "recipe-photos";
const MAX_BYTES = 5 * 1024 * 1024; // 5MB

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
    return new Response(JSON.stringify({ ok: false, error: "Method not allowed" }), {
      status: 405,
      headers: { "Content-Type": "application/json", ...CORS_HEADERS },
    });
  }

  try {
    const contentType = req.headers.get("content-type") ?? "application/octet-stream";
    if (!contentType.startsWith("image/")) {
      return new Response(JSON.stringify({ ok: false, error: "Файлът трябва да е снимка." }), {
        status: 400,
        headers: { "Content-Type": "application/json", ...CORS_HEADERS },
      });
    }

    const bytes = new Uint8Array(await req.arrayBuffer());
    if (bytes.byteLength === 0) {
      return new Response(JSON.stringify({ ok: false, error: "Празен файл." }), {
        status: 400,
        headers: { "Content-Type": "application/json", ...CORS_HEADERS },
      });
    }
    if (bytes.byteLength > MAX_BYTES) {
      return new Response(JSON.stringify({ ok: false, error: "Снимката е твърде голяма (макс. 5MB)." }), {
        status: 400,
        headers: { "Content-Type": "application/json", ...CORS_HEADERS },
      });
    }

    const ext = contentType.split("/")[1]?.split("+")[0] ?? "jpg";
    const path = `${crypto.randomUUID()}.${ext}`;

    const uploadRes = await fetch(`${SUPABASE_URL}/storage/v1/object/${BUCKET}/${path}`, {
      method: "POST",
      headers: {
        apikey: SERVICE_ROLE_KEY,
        Authorization: `Bearer ${SERVICE_ROLE_KEY}`,
        "Content-Type": contentType,
      },
      body: bytes,
    });

    if (!uploadRes.ok) {
      const errText = await uploadRes.text();
      throw new Error(`Storage upload failed: ${uploadRes.status} ${errText}`);
    }

    const publicUrl = `${SUPABASE_URL}/storage/v1/object/public/${BUCKET}/${path}`;
    return new Response(JSON.stringify({ ok: true, url: publicUrl }), {
      headers: { "Content-Type": "application/json", ...CORS_HEADERS },
    });
  } catch (err) {
    console.error(err);
    return new Response(JSON.stringify({ ok: false, error: String(err) }), {
      status: 500,
      headers: { "Content-Type": "application/json", ...CORS_HEADERS },
    });
  }
});
