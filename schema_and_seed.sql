-- ============================================================
-- Седмица: Supabase schema + seed data
-- Run this whole script once in Supabase → SQL Editor → New query
--
-- This reflects the live schema as of the multi-household + Auth pivot:
-- every household-scoped table is guarded by is_household_member() via
-- Supabase Auth (auth.uid()), not by permissive "allow all" policies.
-- ============================================================

-- 1. HOUSEHOLDS (each user creates or joins one via the RPCs in section 8;
--    invite_code is how another user joins an existing household)
create table if not exists households (
  id bigint generated always as identity primary key,
  name text not null default 'Моето домакинство',
  invite_code text unique,
  created_at timestamptz default now()
);

-- 1b. HOUSEHOLD MEMBERS (join table between auth.users and households)
create table if not exists household_members (
  id bigint generated always as identity primary key,
  household_id bigint references households(id) on delete cascade,
  user_id uuid references auth.users(id) on delete cascade,
  role text not null default 'member',
  joined_at timestamptz default now(),
  unique (household_id, user_id)
);

-- 2. RECIPES (household_id null = global built-in library, visible to everyone;
--    non-null = a household's own recipe. share_code/servings/portion_grams
--    back the Recipe Book "share with a code" + macros-per-portion features.
--    photo_url/instructions back the recipe-journal photo + "Начин на
--    приготвяне" fields — photo_url points into the public recipe-photos
--    Storage bucket, uploaded via the upload-recipe-photo Edge Function.)
create table if not exists recipes (
  id bigint generated always as identity primary key,
  name text not null,
  source_url text,
  household_id bigint references households(id) on delete cascade,
  share_code text unique,
  servings int,
  portion_grams numeric,
  photo_url text,
  instructions text,
  calories int,
  protein_g numeric(6,1),
  carbs_g numeric(6,1),
  fat_g numeric(6,1),
  created_at timestamptz default now()
);
-- A plain `unique(name)` was global across every household, so two
-- unrelated households could never both have e.g. a recipe named "Супа" —
-- and it doubled as a cross-household existence oracle. Partial indexes
-- instead scope uniqueness correctly: names unique among the global
-- library, and independently unique within each household. (A bare
-- `unique(household_id, name)` wouldn't work here — Postgres treats every
-- null household_id as distinct, so it wouldn't dedupe the global library.)
create unique index if not exists recipes_global_name_key on recipes(name) where household_id is null;
create unique index if not exists recipes_household_name_key on recipes(household_id, name) where household_id is not null;

-- 3. RECIPE INGREDIENTS (one row per ingredient per recipe)
create table if not exists recipe_ingredients (
  id bigint generated always as identity primary key,
  recipe_id bigint references recipes(id) on delete cascade,
  ingredient_name text not null,
  amount text default '',
  sort_order int default 0
);

-- 4. PANTRY ITEMS
create table if not exists pantry_items (
  id bigint generated always as identity primary key,
  household_id bigint references households(id) on delete cascade,
  ingredient_name text not null,
  amount text default '',
  created_at timestamptz default now()
);

-- 4b. MEAL PLAN ENTRIES (one row per meal added to a day, scoped to a
--     specific week so ‹ › navigation shows different weeks instead of one
--     repeating template. sort_order is what preserves the order meals were
--     added to that day. meal_type is a leftover from an earlier design with
--     fixed Закуска/Обяд/Вечеря slots — the app no longer uses fixed slots
--     (a day can hold any number of meals), so this column is always written
--     as an empty string and kept only because dropping it isn't worth a
--     migration right now.)
create table if not exists meal_plan_entries (
  id bigint generated always as identity primary key,
  household_id bigint references households(id) on delete cascade,
  week_start date not null default (date_trunc('week', now()))::date,  -- Monday of the week this entry belongs to
  day_key text not null,       -- 'Monday' .. 'Sunday'
  meal_type text not null default '',  -- unused, see comment above
  meal_name text not null,
  sort_order int default 0,
  created_at timestamptz default now()
);
create index if not exists idx_meal_plan_entries_household_week on meal_plan_entries(household_id, week_start);

-- 4c. FAVORITE RECIPES (per household, for quick re-adding to the plan)
create table if not exists favorite_recipes (
  id bigint generated always as identity primary key,
  household_id bigint references households(id) on delete cascade,
  recipe_name text not null,
  created_at timestamptz default now(),
  unique (household_id, recipe_name)
);

-- 4d. SUBSCRIPTIONS (one per household — billing is per household, not per
--     user, matching how everything else in this app is shared. 'trial' and
--     'active' both grant full access; anything else shows the paywall.
--     Every household gets a 30-day trial row on creation, via
--     create_household_for_user() below. Only the stripe-webhook Edge
--     Function writes to this table after that, using the service-role key
--     — there's deliberately no client-facing insert/update policy, so a
--     household can't fake its own subscription status.)
create table if not exists subscriptions (
  id bigint generated always as identity primary key,
  household_id bigint not null unique references households(id) on delete cascade,
  stripe_customer_id text,
  stripe_subscription_id text,
  status text not null default 'trial' check (status in ('trial', 'active', 'past_due', 'canceled')),
  trial_ends_at timestamptz,
  current_period_end timestamptz,
  created_at timestamptz default now()
);

-- 5. PRICE DATA (refreshed daily by the sync-prices Edge Function — see
--    section 9 — from live store-brochure promotions; product_name/old_price
--    let the Промоции tab show every matched branded product per ingredient,
--    not just a single "cheapest" pick)
create table if not exists price_data (
  id bigint generated always as identity primary key,
  ingredient_name text not null,
  store_name text not null,
  product_name text,
  price numeric(10,2),
  old_price numeric(10,2),
  unit text default '',
  updated_at timestamptz default now()
);

-- 6. is_household_member(): the single source of truth every RLS policy
--    below defers to, so "can this user see/edit this row" is defined once.
create or replace function is_household_member(p_household_id bigint)
returns boolean
language sql stable security definer set search_path to 'public'
as $$
  select exists (
    select 1 from household_members
    where household_id = p_household_id and user_id = auth.uid()
  );
$$;

-- 7. ROW LEVEL SECURITY — every household-scoped table is gated by
--    is_household_member(); the global recipe library (household_id is null)
--    stays readable by anyone, and price_data is readable by any signed-in user.
alter table households enable row level security;
alter table household_members enable row level security;
alter table recipes enable row level security;
alter table recipe_ingredients enable row level security;
alter table pantry_items enable row level security;
alter table meal_plan_entries enable row level security;
alter table favorite_recipes enable row level security;
alter table price_data enable row level security;
alter table subscriptions enable row level security;

create policy "Members can view their households" on households
  for select using (is_household_member(id));

create policy "Members can view their membership rows" on household_members
  for select using (user_id = auth.uid() or is_household_member(household_id));

create policy "View recipes: global or own household" on recipes
  for select using (household_id is null or is_household_member(household_id));
create policy "Manage own household recipes" on recipes
  for insert with check (is_household_member(household_id));
create policy "Update own household recipes" on recipes
  for update using (is_household_member(household_id));
create policy "Delete own household recipes" on recipes
  for delete using (is_household_member(household_id));

create policy "View ingredients of visible recipes" on recipe_ingredients
  for select using (exists (
    select 1 from recipes r
    where r.id = recipe_ingredients.recipe_id
      and (r.household_id is null or is_household_member(r.household_id))
  ));
create policy "Manage ingredients of own recipes" on recipe_ingredients
  for all using (exists (
    select 1 from recipes r
    where r.id = recipe_ingredients.recipe_id and is_household_member(r.household_id)
  ));

create policy "Household scoped pantry" on pantry_items
  for all using (is_household_member(household_id)) with check (is_household_member(household_id));
create policy "Household scoped meal plan" on meal_plan_entries
  for all using (is_household_member(household_id)) with check (is_household_member(household_id));
create policy "Household scoped favorites" on favorite_recipes
  for all using (is_household_member(household_id)) with check (is_household_member(household_id));

create policy "Price data readable by authenticated users" on price_data
  for select using (auth.role() = 'authenticated');

-- Read-only for members; no insert/update policy at all — only the
-- stripe-webhook Edge Function (service-role key, bypasses RLS) writes here.
create policy "Household scoped subscription read" on subscriptions
  for select using (is_household_member(household_id));

-- 7b. RATE LIMITING for the two guessable-code RPCs below
--     (join_household_by_code, import_recipe_by_code). Both return NULL for
--     a wrong code instead of raising an exception — an uncaught RAISE
--     rolls back the *whole* function's transaction, which would silently
--     wipe out the attempt-log insert below along with it, defeating the
--     entire point of counting failed guesses.
create table if not exists rpc_rate_limits (
  id bigint generated always as identity primary key,
  user_id uuid not null,
  action text not null,
  attempted_at timestamptz not null default now()
);
create index if not exists idx_rpc_rate_limits_user_action_time on rpc_rate_limits(user_id, action, attempted_at);
alter table rpc_rate_limits enable row level security;
-- No policies on purpose: only the SECURITY DEFINER functions below (which
-- bypass RLS) ever touch this table — it's never exposed via the client REST API.

select cron.unschedule('rpc-rate-limits-cleanup') where exists (select 1 from cron.job where jobname = 'rpc-rate-limits-cleanup');
select cron.schedule(
  'rpc-rate-limits-cleanup',
  '30 3 * * *',
  $$ delete from rpc_rate_limits where attempted_at < now() - interval '1 day'; $$
);

-- 8. RPC FUNCTIONS — the household onboarding + recipe sharing flows all
--    go through SECURITY DEFINER functions rather than raw inserts, since
--    e.g. "join a household" needs to bypass the RLS that would otherwise
--    stop a brand-new member from inserting their own membership row.
create or replace function create_household_for_user(p_name text default 'Моето домакинство')
returns households
language plpgsql security definer set search_path to 'public'
as $$
declare
  h households;
  new_code text;
begin
  -- 10 chars from two independent md5 draws (~52 bits of entropy) rather
  -- than 6 (~25 bits) — a 6-char code was brute-forceable by any
  -- authenticated user via repeated join_household_by_code calls.
  loop
    new_code := upper(substr(md5(random()::text), 1, 6) || substr(md5(random()::text), 1, 4));
    exit when not exists (select 1 from households where invite_code = new_code);
  end loop;
  insert into households (name, invite_code) values (p_name, new_code) returning * into h;
  insert into household_members (household_id, user_id, role) values (h.id, auth.uid(), 'owner');
  insert into subscriptions (household_id, status, trial_ends_at) values (h.id, 'trial', now() + interval '30 days');
  return h;
end;
$$;

create or replace function join_household_by_code(p_code text)
returns households
language plpgsql security definer set search_path to 'public'
as $$
declare
  h households;
  recent_attempts int;
begin
  select count(*) into recent_attempts from rpc_rate_limits
  where user_id = auth.uid() and action = 'join_household_by_code'
    and attempted_at > now() - interval '15 minutes';
  if recent_attempts >= 10 then
    raise exception 'Too many attempts. Please wait a few minutes and try again.';
  end if;
  insert into rpc_rate_limits (user_id, action) values (auth.uid(), 'join_household_by_code');

  select * into h from households where invite_code = upper(p_code);
  if not found then
    return null;
  end if;
  insert into household_members (household_id, user_id, role)
  values (h.id, auth.uid(), 'member')
  on conflict (household_id, user_id) do nothing;
  return h;
end;
$$;

create or replace function share_recipe(p_recipe_id bigint)
returns text
language plpgsql security definer set search_path to 'public'
as $$
declare
  r recipes;
  new_code text;
  caller_household bigint;
begin
  select household_id into caller_household from household_members where user_id = auth.uid() limit 1;
  select * into r from recipes where id = p_recipe_id;
  if not found then
    raise exception 'Recipe not found';
  end if;
  -- Plain `<>` is false (not true) when caller_household is null (no
  -- household yet), silently letting anyone extract a share code for any
  -- private recipe by guessing its id. `is distinct from` treats null
  -- correctly, so a caller with no household is rejected here too.
  if r.household_id is not null and r.household_id is distinct from caller_household then
    raise exception 'Not your recipe';
  end if;
  if r.share_code is not null then
    return r.share_code;
  end if;
  loop
    new_code := upper(substr(md5(random()::text), 1, 6) || substr(md5(random()::text), 1, 4));
    exit when not exists (select 1 from recipes where share_code = new_code);
  end loop;
  update recipes set share_code = new_code where id = p_recipe_id;
  return new_code;
end;
$$;

create or replace function import_recipe_by_code(p_code text)
returns recipes
language plpgsql security definer set search_path to 'public'
as $$
declare
  src recipes;
  dest recipes;
  caller_household bigint;
  final_name text;
  recent_attempts int;
begin
  select count(*) into recent_attempts from rpc_rate_limits
  where user_id = auth.uid() and action = 'import_recipe_by_code'
    and attempted_at > now() - interval '15 minutes';
  if recent_attempts >= 10 then
    raise exception 'Too many attempts. Please wait a few minutes and try again.';
  end if;
  insert into rpc_rate_limits (user_id, action) values (auth.uid(), 'import_recipe_by_code');

  select household_id into caller_household from household_members where user_id = auth.uid() limit 1;
  if caller_household is null then
    raise exception 'You must belong to a household first';
  end if;
  select * into src from recipes where share_code = upper(p_code);
  if not found then
    return null;
  end if;

  final_name := src.name;
  if exists (
    select 1 from recipes
    where name = final_name and (household_id is null or household_id = caller_household)
  ) then
    final_name := src.name || ' (импортирана)';
  end if;

  insert into recipes (name, source_url, household_id, photo_url, instructions, calories, protein_g, carbs_g, fat_g)
  values (final_name, src.source_url, caller_household, src.photo_url, src.instructions, src.calories, src.protein_g, src.carbs_g, src.fat_g)
  returning * into dest;
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order)
  select dest.id, ingredient_name, amount, sort_order from recipe_ingredients where recipe_id = src.id;
  return dest;
end;
$$;

-- 8a. OWNER vs MEMBER — household_members.role existed but nothing checked
--     it. is_household_owner() is the owner-scoped counterpart to
--     is_household_member(), used below for actions that should be an
--     owner's call: removing another member, managing billing.
create or replace function is_household_owner(p_household_id bigint)
returns boolean
language sql stable security definer set search_path to 'public'
as $$
  select exists (
    select 1 from household_members
    where household_id = p_household_id and user_id = auth.uid() and role = 'owner'
  );
$$;

create or replace function leave_household()
returns void
language plpgsql security definer set search_path to 'public'
as $$
declare
  hid bigint;
  was_owner boolean;
  member_count int;
  new_owner uuid;
begin
  select household_id, (role = 'owner') into hid, was_owner
  from household_members where user_id = auth.uid() limit 1;
  if hid is null then
    raise exception 'Not a member of any household';
  end if;

  select count(*) into member_count from household_members where household_id = hid;
  if member_count <= 1 then
    raise exception 'You are the only member of this household';
  end if;

  delete from household_members where household_id = hid and user_id = auth.uid();

  -- The household must always have an owner — if the one who just left was
  -- the last owner, hand it to whoever's been a member the longest.
  if was_owner and not exists (select 1 from household_members where household_id = hid and role = 'owner') then
    select user_id into new_owner from household_members where household_id = hid order by joined_at asc limit 1;
    update household_members set role = 'owner' where household_id = hid and user_id = new_owner;
  end if;
end;
$$;

-- Owner-only: no UI wired up to this yet (needs a member-listing feature
-- first, since there's currently no way for the client to see who else is
-- in the household), but the policy itself is real and enforced now.
create or replace function remove_household_member(p_user_id uuid)
returns void
language plpgsql security definer set search_path to 'public'
as $$
declare
  hid bigint;
begin
  select household_id into hid from household_members
  where user_id = auth.uid() and role = 'owner' limit 1;
  if hid is null then
    raise exception 'Only the household owner can remove members';
  end if;
  if p_user_id = auth.uid() then
    raise exception 'Use leave_household to remove yourself';
  end if;
  delete from household_members where household_id = hid and user_id = p_user_id;
end;
$$;

-- 8b. RECIPE PHOTOS — a public Storage bucket for recipe photos. Note:
--     storage.objects is owned by Supabase's internal storage role, so this
--     project's SQL access can't grant it a client-side RLS insert policy
--     (attempting one fails with "must be owner of table objects"). Uploads
--     instead go through the upload-recipe-photo Edge Function, which writes
--     with the service-role key server-side — Supabase's default verify-jwt
--     gate on Edge Functions still means only a logged-in user can call it.
insert into storage.buckets (id, name, public)
values ('recipe-photos', 'recipe-photos', true)
on conflict (id) do nothing;

-- 9. PROMOTIONS SYNC SCHEDULE — pg_cron fires the sync-prices Edge Function
--    once a day so price_data refreshes automatically, independent of any
--    client session (see supabase/functions/sync-prices/index.ts).
create extension if not exists pg_cron with schema extensions;
create extension if not exists pg_net with schema extensions;

select cron.unschedule('sync-prices-daily') where exists (select 1 from cron.job where jobname = 'sync-prices-daily');

select cron.schedule(
  'sync-prices-daily',
  '15 6 * * *',
  $$
  select net.http_post(
    url := 'https://xdrrumrjrbihidqniyvc.supabase.co/functions/v1/sync-prices',
    headers := jsonb_build_object(
      'Content-Type', 'application/json',
      'apikey', 'sb_publishable_CEoX6qcAbd-JfOJT1oB7bw_r7StYdcc',
      'Authorization', 'Bearer sb_publishable_CEoX6qcAbd-JfOJT1oB7bw_r7StYdcc',
      -- Must match the SYNC_PRICES_SECRET function secret — the apikey/
      -- Authorization headers above only satisfy Supabase's platform verify-jwt
      -- gate (which accepts the public anon key), not real caller identity, so
      -- the function itself checks this header before doing any work.
      'X-Sync-Secret', '5d52cb0b931b730944e0adc5ee5e7785bedde00914821048'
    ),
    body := '{}'::jsonb
  );
  $$
);

-- 10. SEED DATA: the 21 built-in library recipes (household_id null, so
--     they're visible to every household), source: gotvach.bg, used as
--     demo/starter content. These rows exist so the client's embedded copy
--     (MealApp.Shared/wwwroot/data/recipes-demo.json, always loaded first
--     for instant startup) gets matching real database ids and ingredient
--     rows once Supabase syncs. calories/protein_g/carbs_g/fat_g are
--     duplicated here from that same JSON file, hand-curated per recipe —
--     keep the two in sync if either changes.

insert into recipes (name, calories, protein_g, carbs_g, fat_g) values ('Шкембе чорба с прясно мляко', 380, 28, 12, 24);
insert into recipes (name, calories, protein_g, carbs_g, fat_g) values ('Шопска Салата Оригинал', 220, 8, 12, 16);
insert into recipes (name, calories, protein_g, carbs_g, fat_g) values ('Класически пържени кюфтета', 420, 24, 18, 27);
insert into recipes (name, calories, protein_g, carbs_g, fat_g) values ('Класическа мусака с картофи и кайма', 520, 22, 38, 30);
insert into recipes (name, calories, protein_g, carbs_g, fat_g) values ('Класическа овчарска салата', 380, 22, 14, 26);
insert into recipes (name, calories, protein_g, carbs_g, fat_g) values ('Пилешка супа с фиде и застройка', 310, 20, 30, 11);
insert into recipes (name, calories, protein_g, carbs_g, fat_g) values ('Пълнени чушки с кайма, ориз и бял сос', 460, 20, 34, 26);
insert into recipes (name, calories, protein_g, carbs_g, fat_g) values ('Класически Български Таратор', 140, 6, 10, 8);
insert into recipes (name, calories, protein_g, carbs_g, fat_g) values ('Кебапчета', 360, 26, 4, 26);
insert into recipes (name, calories, protein_g, carbs_g, fat_g) values ('Боб яхнията на баба', 340, 14, 46, 12);
insert into recipes (name, calories, protein_g, carbs_g, fat_g) values ('Свински пържоли на тиган', 420, 32, 2, 30);
insert into recipes (name, calories, protein_g, carbs_g, fat_g) values ('Лек зеленчуков гювеч', 210, 6, 30, 8);
insert into recipes (name, calories, protein_g, carbs_g, fat_g) values ('Вкусна леща яхния', 320, 16, 46, 8);
insert into recipes (name, calories, protein_g, carbs_g, fat_g) values ('Пиле с картофи на фурна по селски', 520, 30, 40, 26);
insert into recipes (name, calories, protein_g, carbs_g, fat_g) values ('Зелеви сарми с кайма и ориз', 400, 18, 32, 22);
insert into recipes (name, calories, protein_g, carbs_g, fat_g) values ('Перфектната скумрия на скара', 340, 30, 2, 22);
insert into recipes (name, calories, protein_g, carbs_g, fat_g) values ('Салата Снежанка', 260, 8, 12, 20);
insert into recipes (name, calories, protein_g, carbs_g, fat_g) values ('Ориз със спанак', 260, 6, 46, 6);
insert into recipes (name, calories, protein_g, carbs_g, fat_g) values ('Класическа зеле и моркови салата', 140, 2, 16, 8);
insert into recipes (name, calories, protein_g, carbs_g, fat_g) values ('Салата от печено цвекло', 160, 3, 14, 10);
insert into recipes (name, calories, protein_g, carbs_g, fat_g) values ('Пиле с ориз - класическа рецепта', 480, 28, 44, 20);

-- Ingredients, looked up by recipe name so ids don't need to match by hand
do $$
declare
  r_id bigint;
begin
  select id into r_id from recipes where name = 'Шкембе чорба с прясно мляко';
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'телешко шкембе', '1 кг', 0);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'прясно мляко', '1 литър', 1);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'олио', '2 с.л.', 2);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'масло', '2 с.л.', 3);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'червен пипер', '1 1/2 ч.л.', 4);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'сол', '', 5);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'чесън', '4-5 скилидки', 6);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'оцет', '~100 мл', 7);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'лют червен пипер', '', 8);

  select id into r_id from recipes where name = 'Шопска Салата Оригинал';
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'домати', '400 г', 0);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'краставици', '250 г', 1);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'печени чушки', '1 бр.', 2);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'кромид лук', '1 глава', 3);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'краве сирене', '200 г', 4);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'магданоз', '1/2 връзка', 5);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'оцет', '', 6);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'олио', '', 7);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'сол', '', 8);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'маслини', '', 9);

  select id into r_id from recipes where name = 'Класически пържени кюфтета';
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'кайма', '500 г смес', 0);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'лук', '1 глава', 1);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'яйца', '1 бр.', 2);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'стар хляб', '2 филийки', 3);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'магданоз', '', 4);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'черен пипер', '', 5);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'сол', '', 6);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'брашно', 'за овалване', 7);

  select id into r_id from recipes where name = 'Класическа мусака с картофи и кайма';
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'лук', '3 глави', 0);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'чесън', '4 скилидки', 1);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'кайма', '1 кг', 2);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'картофи', '1 кг', 3);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'олио', '80 мл', 4);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'доматена паста', '4 с.л.', 5);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'сол', '', 6);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'черен пипер', '', 7);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'чубрица', '', 8);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'червен пипер', '', 9);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'кимион', 'по желание', 10);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'захар', 'щипка', 11);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'магданоз', '', 12);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'яйца', '3 бр. (заливка)', 13);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'кисело мляко', '350 г (заливка)', 14);

  select id into r_id from recipes where name = 'Класическа овчарска салата';
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'домати', '4 бр.', 0);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'краставици', '1 бр.', 1);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'чушки', '3 бр. печени', 2);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'шунка', '300 г', 3);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'сирене', '300 г', 4);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'кашкавал', '300 г', 5);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'царевица', '1 малка кутийка', 6);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'гъби', '1/2 буркан мариновани', 7);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'маслини', '12 бр.', 8);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'магданоз', '2-3 стръка', 9);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'лук', '1 глава', 10);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'олио', '', 11);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'оцет', '', 12);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'сол', '', 13);

  select id into r_id from recipes where name = 'Пилешка супа с фиде и застройка';
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'пилешко филе', '350 г', 0);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'картофи', '2-3 бр.', 1);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'моркови', '1 бр.', 2);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'лук', '1 глава', 3);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'фиде', '2 шепи', 4);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'олио', '2-3 с.л.', 5);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'сол', '', 6);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'черен пипер', '', 7);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'чубрица', '1 с.л.', 8);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'джоджен', '1-2 щипки', 9);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'девесил', '1 щипка', 10);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'магданоз', '1 ч.л.', 11);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'жълтъци', '1 бр. (застройка)', 12);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'кисело мляко', '4-5 с.л. (застройка)', 13);

  select id into r_id from recipes where name = 'Пълнени чушки с кайма, ориз и бял сос';
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'чушки', '10-12 бр.', 0);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'кайма', '500 г', 1);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'ориз', '1 ч.ч.', 2);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'червен пипер', '1 ч.л.', 3);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'домати', '100 г консервирани', 4);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'лук', '1 глава', 5);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'моркови', '1-2 бр.', 6);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'чесън', '2-3 скилидки', 7);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'магданоз', '2-3 стръка', 8);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'чубрица', '1 ч.л.', 9);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'олио', '4 с.л.', 10);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'брашно', '3-4 с.л. (сос)', 11);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'яйца', '3 бр. (сос)', 12);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'кисело мляко', '1 ч.ч. (сос)', 13);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'сол', '', 14);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'черен пипер', 'на вкус', 15);

  select id into r_id from recipes where name = 'Класически Български Таратор';
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'краставици', '1 бр.', 0);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'кисело мляко', '400 г', 1);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'вода', '400 мл', 2);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'пресен чесън', '2 стръка', 3);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'копър', '5-6 стръка', 4);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'магданоз', '5-6 стръка', 5);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'сол', '', 6);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'олио', 'или зехтин', 7);

  select id into r_id from recipes where name = 'Кебапчета';
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'кайма', '500 г', 0);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'кимион', '1.5 ч.л.', 1);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'чесън', '4 скилидки', 2);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'сол', '', 3);

  select id into r_id from recipes where name = 'Боб яхнията на баба';
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'боб', '2 ч.ч.', 0);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'моркови', '1 голям', 1);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'лук', '1 голяма глава', 2);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'чесън', '4 скилидки', 3);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'олио', '1/2 ч.ч.', 4);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'червен пипер', '2 ч.л.', 5);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'джоджен', '1 ч.л.', 6);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'сол', '1 ч.л.', 7);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'домати', '2 бр.', 8);

  select id into r_id from recipes where name = 'Свински пържоли на тиган';
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'свински пържоли', '6 бр.', 0);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'олио', '60 мл', 1);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'чесън', '2 скилидки', 2);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'сол', '2 щипки', 3);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'черен пипер', '1/4 ч.л.', 4);

  select id into r_id from recipes where name = 'Лек зеленчуков гювеч';
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'картофи', '700 г', 0);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'моркови', '2 бр.', 1);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'лук', '2 глави', 2);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'червена чушка', '1 бр.', 3);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'зелена чушка', '1 бр.', 4);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'грах', '150 г', 5);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'гъби', '200 г', 6);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'домати', '300 г от консерва', 7);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'олио', '70 мл', 8);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'чесън', '3 скилидки', 9);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'червен пипер', '1 ч.л.', 10);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'чубрица', '1 ч.л.', 11);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'магданоз', '1/2 връзка', 12);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'черен пипер', '1/2 ч.л.', 13);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'сол', 'на вкус', 14);

  select id into r_id from recipes where name = 'Вкусна леща яхния';
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'леща', '1 купичка', 0);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'лук', '3 големи глави', 1);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'моркови', '2 големи', 2);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'чесън', '4-5 големи скилидки', 3);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'чушки', '1 червена', 4);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'домати', '3 с.л. от консерва', 5);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'сол', '1 ч.л.', 6);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'чубрица', '1 ч.л.', 7);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'джоджен', '1 ч.л.', 8);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'червен пипер', '1 с.л.', 9);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'брашно', '4 с.л.', 10);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'олио', '', 11);

  select id into r_id from recipes where name = 'Пиле с картофи на фурна по селски';
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'пиле', '1 бр.', 0);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'картофи', '1.5 кг', 1);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'олио', '50 г', 2);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'краве масло', '100 г', 3);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'лук', '1 глава', 4);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'моркови', '1 бр.', 5);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'черен пипер', '1/4 ч.л.', 6);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'червен пипер', '1/2 ч.л.', 7);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'мащерка', '1 ч.л.', 8);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'сол', '1 с.л.', 9);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'универсална подправка', '1 ч.л.', 10);

  select id into r_id from recipes where name = 'Зелеви сарми с кайма и ориз';
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'кисело зеле', '1 бр.', 0);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'кайма', '500 г смес', 1);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'ориз', '1 ч.ч.', 2);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'лук', '1 глава', 3);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'червен пипер', '', 4);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'черен пипер', '', 5);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'риган', '', 6);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'чубрица', '', 7);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'сол', '', 8);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'олио', '', 9);

  select id into r_id from recipes where name = 'Перфектната скумрия на скара';
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'скумрия', '6 риби', 0);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'сол', '1 ч.л.', 1);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'лимонов сок', '6 с.л.', 2);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'черен пипер', '1/2 ч.л.', 3);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'бял пипер', '1 щипка', 4);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'розов пипер', '1 щипка', 5);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'олио', '1 ч.л.', 6);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'лимон', 'за сервиране', 7);

  select id into r_id from recipes where name = 'Салата Снежанка';
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'кисело мляко', '2.2 кг', 0);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'кисели краставички', '650 г', 1);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'чесън', '4 скилидки', 2);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'сол', '2 ч.л.', 3);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'олио', '80 мл', 4);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'орехи', '80 г', 5);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'копър', '1/2 връзка', 6);

  select id into r_id from recipes where name = 'Ориз със спанак';
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'ориз', '1 ч.ч.', 0);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'спанак', '600 г', 1);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'сол', '', 2);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'черен пипер', '', 3);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'бульон', '1 кубче', 4);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'олио', '', 5);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'лук', '1/2 глава', 6);

  select id into r_id from recipes where name = 'Класическа зеле и моркови салата';
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'зеле', '500 г', 0);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'моркови', '4 бр.', 1);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'олио', '50 мл', 2);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'оцет', 'на вкус', 3);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'сол', 'на вкус', 4);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'магданоз', '1 връзка', 5);

  select id into r_id from recipes where name = 'Салата от печено цвекло';
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'червено цвекло', '1 бр.', 0);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'чесън', '4-5 скилидки', 1);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'зехтин', '', 2);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'балсамов оцет', '', 3);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'сол', '', 4);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'магданоз', '', 5);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'орехи', '', 6);

  select id into r_id from recipes where name = 'Пиле с ориз - класическа рецепта';
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'пиле', '1 бр. (~1.5 кг)', 0);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'лук', '2 глави', 1);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'моркови', '1 бр.', 2);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'чушки', '1 бр.', 3);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'ориз', '400 г', 4);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'олио', '40 мл', 5);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'масло', '40 г', 6);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'черен пипер', '10 зърна', 7);
  insert into recipe_ingredients (recipe_id, ingredient_name, amount, sort_order) values (r_id, 'сол', 'на вкус', 8);

end $$;
