// Pulls current brochure promotions from the public naoferta.net (sofia-supermarkets-api)
// aggregator and refreshes the `price_data` table used by the Промоции tab.
// Triggered on a schedule by pg_cron (see "9. PROMOTIONS SYNC SCHEDULE" in schema_and_seed.sql).
//
// Unlike a price-comparison feature, this keeps every matched branded product per
// store/ingredient (not just the cheapest) — the user picks the brand themselves.

const SUPABASE_URL = Deno.env.get("SUPABASE_URL")!;
const SERVICE_ROLE_KEY = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!;

interface Product {
  name: string;
  quantity: string | null;
  price: number | null;
  oldPrice: number | null;
}
interface ProductStore {
  supermarket: string;
  products: Product[];
}
interface MatchedRow {
  ingredient_name: string;
  store_name: string;
  product_name: string;
  price: number;
  old_price: number | null;
  unit: string;
}

function normalize(s: string): string {
  return s.toLowerCase().replace(/^[-\s]+/, "").trim();
}

Deno.serve(async (_req: Request) => {
  try {
    const ingRes = await fetch(
      `${SUPABASE_URL}/rest/v1/recipe_ingredients?select=ingredient_name`,
      { headers: { apikey: SERVICE_ROLE_KEY, Authorization: `Bearer ${SERVICE_ROLE_KEY}` } },
    );
    if (!ingRes.ok) throw new Error(`ingredients fetch failed: ${ingRes.status}`);
    const ingRows: { ingredient_name: string }[] = await ingRes.json();
    const ingredientNames = [...new Set(ingRows.map((r) => normalize(r.ingredient_name)))]
      .filter((n) => n.length >= 3);

    const prodRes = await fetch("https://api.naoferta.net/products");
    if (!prodRes.ok) throw new Error(`naoferta fetch failed: ${prodRes.status}`);
    const stores: ProductStore[] = await prodRes.json();

    const seen = new Map<string, MatchedRow>();

    for (const store of stores) {
      for (const product of store.products) {
        if (product.price == null) continue;
        const productName = normalize(product.name);
        for (const ingredient of ingredientNames) {
          if (productName.includes(ingredient)) {
            const key = `${ingredient}|${store.supermarket}|${productName}`;
            if (!seen.has(key)) {
              seen.set(key, {
                ingredient_name: ingredient,
                store_name: store.supermarket,
                product_name: product.name.replace(/^[-\s]+/, "").trim(),
                price: product.price,
                old_price: product.oldPrice ?? null,
                unit: product.quantity ?? "",
              });
            }
          }
        }
      }
    }

    const rows = [...seen.values()];

    const delRes = await fetch(`${SUPABASE_URL}/rest/v1/price_data?id=gte.0`, {
      method: "DELETE",
      headers: { apikey: SERVICE_ROLE_KEY, Authorization: `Bearer ${SERVICE_ROLE_KEY}` },
    });
    if (!delRes.ok) throw new Error(`delete failed: ${delRes.status}`);

    if (rows.length > 0) {
      const insRes = await fetch(`${SUPABASE_URL}/rest/v1/price_data`, {
        method: "POST",
        headers: {
          apikey: SERVICE_ROLE_KEY,
          Authorization: `Bearer ${SERVICE_ROLE_KEY}`,
          "Content-Type": "application/json",
          Prefer: "return=minimal",
        },
        body: JSON.stringify(rows),
      });
      if (!insRes.ok) throw new Error(`insert failed: ${insRes.status} ${await insRes.text()}`);
    }

    return new Response(
      JSON.stringify({ ok: true, matched: rows.length, stores: stores.map((s) => s.supermarket) }),
      { headers: { "Content-Type": "application/json" } },
    );
  } catch (err) {
    console.error(err);
    return new Response(JSON.stringify({ ok: false, error: String(err) }), {
      status: 500,
      headers: { "Content-Type": "application/json" },
    });
  }
});
