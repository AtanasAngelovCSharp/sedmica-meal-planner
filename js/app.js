// ==========================================================================
// LEGACY PROTOTYPE — superseded by the .NET MAUI/Blazor rewrite in Shared/,
// Blazor/, and Maui/. This file (served by the root index.html) is the
// original JS version the app was first built as; it's kept for reference
// only and is no longer deployed or actively maintained.
// ==========================================================================

// ==========================================================================
// SUPABASE CONNECTION
// Paste your project's values below (Supabase dashboard → Project Settings →
// API). Until you do, the app keeps working exactly as before, using the
// built-in RECIPES_DB further down this file — nothing breaks either way.
// ==========================================================================
const SUPABASE_URL = 'https://xdrrumrjrbihidqniyvc.supabase.co';
const SUPABASE_ANON_KEY = 'sb_publishable_CEoX6qcAbd-JfOJT1oB7bw_r7StYdcc';

let supabaseClient = null;
const supabaseConfigured =
  SUPABASE_URL !== 'YOUR_SUPABASE_URL' &&
  SUPABASE_ANON_KEY !== 'YOUR_SUPABASE_ANON_KEY' &&
  typeof window.supabase !== 'undefined';

if (supabaseConfigured) {
  supabaseClient = window.supabase.createClient(SUPABASE_URL, SUPABASE_ANON_KEY);
}

function setDataSourceBadge(state, text) {
  const badge = document.getElementById('dataSourceBadge');
  if (!badge) return;
  badge.classList.remove('is-supabase', 'is-error');
  if (state === 'supabase') badge.classList.add('is-supabase');
  if (state === 'error') badge.classList.add('is-error');
  badge.textContent = text;
}

// Fetches recipes + their ingredients from Supabase and merges them into
// RECIPES_DB (declared further down). Falls back to the built-in data —
// silently if Supabase simply isn't configured yet, loudly (in the console)
// if it IS configured but something went wrong, so real errors aren't hidden.
async function loadRecipesFromSupabase() {
  if (!supabaseConfigured) {
    setDataSourceBadge('local', '📦 Вградени демо рецепти (Supabase все още не е свързан)');
    return false;
  }

  try {
    const { data: recipes, error: recipesError } = await supabaseClient
      .from('recipes')
      .select('id, name');
    if (recipesError) throw recipesError;

    const { data: ingredients, error: ingredientsError } = await supabaseClient
      .from('recipe_ingredients')
      .select('recipe_id, ingredient_name, amount, sort_order')
      .order('sort_order', { ascending: true });
    if (ingredientsError) throw ingredientsError;

    if (!recipes || recipes.length === 0) {
      setDataSourceBadge('error', '⚠️ Supabase е свързан, но таблицата "recipes" е празна — пусни schema_and_seed.sql');
      return false;
    }

    const idToName = {};
    recipes.forEach(r => { idToName[r.id] = r.name; });

    const freshDB = {};
    recipes.forEach(r => { freshDB[r.name] = []; });
    (ingredients || []).forEach(ing => {
      const recipeName = idToName[ing.recipe_id];
      if (!recipeName) return;
      freshDB[recipeName].push({ name: ing.ingredient_name, amount: ing.amount || '' });
    });

    Object.assign(RECIPES_DB, freshDB);
    setDataSourceBadge('supabase', `🟢 Supabase свързан · ${recipes.length} рецепти`);
    return true;
  } catch (err) {
    console.error('Supabase connection error:', err);
    setDataSourceBadge('error', '⚠️ Грешка при връзка със Supabase — виж конзолата. Показвам вградените демо рецепти.');
    return false;
  }
}

// ==========================================================================
// TAB SWITCHING
// ==========================================================================
const navButtons = document.querySelectorAll('.nav-btn');
const panels = document.querySelectorAll('.tab-panel');

navButtons.forEach(btn => {
  btn.addEventListener('click', () => {
    navButtons.forEach(b => b.classList.remove('active'));
    panels.forEach(p => p.classList.remove('active'));
    btn.classList.add('active');
    document.getElementById(btn.dataset.tab).classList.add('active');
    if (btn.dataset.tab === 'tab-shopping') renderShoppingList();
  });
});

// ==========================================================================
// RECIPE DATABASE
// Real recipes + ingredient lists sourced from gotvach.bg, used as seed/test
// data while the app doesn't yet have its own recipe database or scraper.
// Each ingredient: { name, amount } — amount is the quantity text as written
// in the original recipe (kept as-is rather than guessing unit conversions).
// ==========================================================================
const RECIPES_DB = {
  "Шкембе чорба с прясно мляко": [
    { name: "телешко шкембе", amount: "1 кг" },
    { name: "прясно мляко", amount: "1 литър" },
    { name: "олио", amount: "2 с.л." },
    { name: "масло", amount: "2 с.л." },
    { name: "червен пипер", amount: "1 1/2 ч.л." },
    { name: "сол", amount: "" },
    { name: "чесън", amount: "4-5 скилидки" },
    { name: "оцет", amount: "~100 мл" },
    { name: "лют червен пипер", amount: "" },
  ],
  "Шопска Салата Оригинал": [
    { name: "домати", amount: "400 г" },
    { name: "краставици", amount: "250 г" },
    { name: "печени чушки", amount: "1 бр." },
    { name: "кромид лук", amount: "1 глава" },
    { name: "краве сирене", amount: "200 г" },
    { name: "магданоз", amount: "1/2 връзка" },
    { name: "оцет", amount: "" },
    { name: "олио", amount: "" },
    { name: "сол", amount: "" },
    { name: "маслини", amount: "" },
  ],
  "Класически пържени кюфтета": [
    { name: "кайма", amount: "500 г смес" },
    { name: "лук", amount: "1 глава" },
    { name: "яйца", amount: "1 бр." },
    { name: "стар хляб", amount: "2 филийки" },
    { name: "магданоз", amount: "" },
    { name: "черен пипер", amount: "" },
    { name: "сол", amount: "" },
    { name: "брашно", amount: "за овалване" },
  ],
  "Класическа мусака с картофи и кайма": [
    { name: "лук", amount: "3 глави" },
    { name: "чесън", amount: "4 скилидки" },
    { name: "кайма", amount: "1 кг" },
    { name: "картофи", amount: "1 кг" },
    { name: "олио", amount: "80 мл" },
    { name: "доматена паста", amount: "4 с.л." },
    { name: "сол", amount: "" },
    { name: "черен пипер", amount: "" },
    { name: "чубрица", amount: "" },
    { name: "червен пипер", amount: "" },
    { name: "кимион", amount: "по желание" },
    { name: "захар", amount: "щипка" },
    { name: "магданоз", amount: "" },
    { name: "яйца", amount: "3 бр. (заливка)" },
    { name: "кисело мляко", amount: "350 г (заливка)" },
  ],
  "Класическа овчарска салата": [
    { name: "домати", amount: "4 бр." },
    { name: "краставици", amount: "1 бр." },
    { name: "чушки", amount: "3 бр. печени" },
    { name: "шунка", amount: "300 г" },
    { name: "сирене", amount: "300 г" },
    { name: "кашкавал", amount: "300 г" },
    { name: "царевица", amount: "1 малка кутийка" },
    { name: "гъби", amount: "1/2 буркан мариновани" },
    { name: "маслини", amount: "12 бр." },
    { name: "магданоз", amount: "2-3 стръка" },
    { name: "лук", amount: "1 глава" },
    { name: "олио", amount: "" },
    { name: "оцет", amount: "" },
    { name: "сол", amount: "" },
  ],
  "Пилешка супа с фиде и застройка": [
    { name: "пилешко филе", amount: "350 г" },
    { name: "картофи", amount: "2-3 бр." },
    { name: "моркови", amount: "1 бр." },
    { name: "лук", amount: "1 глава" },
    { name: "фиде", amount: "2 шепи" },
    { name: "олио", amount: "2-3 с.л." },
    { name: "сол", amount: "" },
    { name: "черен пипер", amount: "" },
    { name: "чубрица", amount: "1 с.л." },
    { name: "джоджен", amount: "1-2 щипки" },
    { name: "девесил", amount: "1 щипка" },
    { name: "магданоз", amount: "1 ч.л." },
    { name: "жълтъци", amount: "1 бр. (застройка)" },
    { name: "кисело мляко", amount: "4-5 с.л. (застройка)" },
  ],
  "Пълнени чушки с кайма, ориз и бял сос": [
    { name: "чушки", amount: "10-12 бр." },
    { name: "кайма", amount: "500 г" },
    { name: "ориз", amount: "1 ч.ч." },
    { name: "червен пипер", amount: "1 ч.л." },
    { name: "домати", amount: "100 г консервирани" },
    { name: "лук", amount: "1 глава" },
    { name: "моркови", amount: "1-2 бр." },
    { name: "чесън", amount: "2-3 скилидки" },
    { name: "магданоз", amount: "2-3 стръка" },
    { name: "чубрица", amount: "1 ч.л." },
    { name: "олио", amount: "4 с.л." },
    { name: "брашно", amount: "3-4 с.л. (сос)" },
    { name: "яйца", amount: "3 бр. (сос)" },
    { name: "кисело мляко", amount: "1 ч.ч. (сос)" },
    { name: "сол", amount: "" },
    { name: "черен пипер", amount: "на вкус" },
  ],
  "Класически Български Таратор": [
    { name: "краставици", amount: "1 бр." },
    { name: "кисело мляко", amount: "400 г" },
    { name: "вода", amount: "400 мл" },
    { name: "пресен чесън", amount: "2 стръка" },
    { name: "копър", amount: "5-6 стръка" },
    { name: "магданоз", amount: "5-6 стръка" },
    { name: "сол", amount: "" },
    { name: "олио", amount: "или зехтин" },
  ],
  "Кебапчета": [
    { name: "кайма", amount: "500 г" },
    { name: "кимион", amount: "1.5 ч.л." },
    { name: "чесън", amount: "4 скилидки" },
    { name: "сол", amount: "" },
  ],
  "Боб яхнията на баба": [
    { name: "боб", amount: "2 ч.ч." },
    { name: "моркови", amount: "1 голям" },
    { name: "лук", amount: "1 голяма глава" },
    { name: "чесън", amount: "4 скилидки" },
    { name: "олио", amount: "1/2 ч.ч." },
    { name: "червен пипер", amount: "2 ч.л." },
    { name: "джоджен", amount: "1 ч.л." },
    { name: "сол", amount: "1 ч.л." },
    { name: "домати", amount: "2 бр." },
  ],
  "Свински пържоли на тиган": [
    { name: "свински пържоли", amount: "6 бр." },
    { name: "олио", amount: "60 мл" },
    { name: "чесън", amount: "2 скилидки" },
    { name: "сол", amount: "2 щипки" },
    { name: "черен пипер", amount: "1/4 ч.л." },
  ],
  "Лек зеленчуков гювеч": [
    { name: "картофи", amount: "700 г" },
    { name: "моркови", amount: "2 бр." },
    { name: "лук", amount: "2 глави" },
    { name: "червена чушка", amount: "1 бр." },
    { name: "зелена чушка", amount: "1 бр." },
    { name: "грах", amount: "150 г" },
    { name: "гъби", amount: "200 г" },
    { name: "домати", amount: "300 г от консерва" },
    { name: "олио", amount: "70 мл" },
    { name: "чесън", amount: "3 скилидки" },
    { name: "червен пипер", amount: "1 ч.л." },
    { name: "чубрица", amount: "1 ч.л." },
    { name: "магданоз", amount: "1/2 връзка" },
    { name: "черен пипер", amount: "1/2 ч.л." },
    { name: "сол", amount: "на вкус" },
  ],
  "Вкусна леща яхния": [
    { name: "леща", amount: "1 купичка" },
    { name: "лук", amount: "3 големи глави" },
    { name: "моркови", amount: "2 големи" },
    { name: "чесън", amount: "4-5 големи скилидки" },
    { name: "чушки", amount: "1 червена" },
    { name: "домати", amount: "3 с.л. от консерва" },
    { name: "сол", amount: "1 ч.л." },
    { name: "чубрица", amount: "1 ч.л." },
    { name: "джоджен", amount: "1 ч.л." },
    { name: "червен пипер", amount: "1 с.л." },
    { name: "брашно", amount: "4 с.л." },
    { name: "олио", amount: "" },
  ],
  "Пиле с картофи на фурна по селски": [
    { name: "пиле", amount: "1 бр." },
    { name: "картофи", amount: "1.5 кг" },
    { name: "олио", amount: "50 г" },
    { name: "краве масло", amount: "100 г" },
    { name: "лук", amount: "1 глава" },
    { name: "моркови", amount: "1 бр." },
    { name: "черен пипер", amount: "1/4 ч.л." },
    { name: "червен пипер", amount: "1/2 ч.л." },
    { name: "мащерка", amount: "1 ч.л." },
    { name: "сол", amount: "1 с.л." },
    { name: "универсална подправка", amount: "1 ч.л." },
  ],
  "Зелеви сарми с кайма и ориз": [
    { name: "кисело зеле", amount: "1 бр." },
    { name: "кайма", amount: "500 г смес" },
    { name: "ориз", amount: "1 ч.ч." },
    { name: "лук", amount: "1 глава" },
    { name: "червен пипер", amount: "" },
    { name: "черен пипер", amount: "" },
    { name: "риган", amount: "" },
    { name: "чубрица", amount: "" },
    { name: "сол", amount: "" },
    { name: "олио", amount: "" },
  ],
  "Перфектната скумрия на скара": [
    { name: "скумрия", amount: "6 риби" },
    { name: "сол", amount: "1 ч.л." },
    { name: "лимонов сок", amount: "6 с.л." },
    { name: "черен пипер", amount: "1/2 ч.л." },
    { name: "бял пипер", amount: "1 щипка" },
    { name: "розов пипер", amount: "1 щипка" },
    { name: "олио", amount: "1 ч.л." },
    { name: "лимон", amount: "за сервиране" },
  ],
  "Салата Снежанка": [
    { name: "кисело мляко", amount: "2.2 кг" },
    { name: "кисели краставички", amount: "650 г" },
    { name: "чесън", amount: "4 скилидки" },
    { name: "сол", amount: "2 ч.л." },
    { name: "олио", amount: "80 мл" },
    { name: "орехи", amount: "80 г" },
    { name: "копър", amount: "1/2 връзка" },
  ],
  "Ориз със спанак": [
    { name: "ориз", amount: "1 ч.ч." },
    { name: "спанак", amount: "600 г" },
    { name: "сол", amount: "" },
    { name: "черен пипер", amount: "" },
    { name: "бульон", amount: "1 кубче" },
    { name: "олио", amount: "" },
    { name: "лук", amount: "1/2 глава" },
  ],
  "Класическа зеле и моркови салата": [
    { name: "зеле", amount: "500 г" },
    { name: "моркови", amount: "4 бр." },
    { name: "олио", amount: "50 мл" },
    { name: "оцет", amount: "на вкус" },
    { name: "сол", amount: "на вкус" },
    { name: "магданоз", amount: "1 връзка" },
  ],
  "Салата от печено цвекло": [
    { name: "червено цвекло", amount: "1 бр." },
    { name: "чесън", amount: "4-5 скилидки" },
    { name: "зехтин", amount: "" },
    { name: "балсамов оцет", amount: "" },
    { name: "сол", amount: "" },
    { name: "магданоз", amount: "" },
    { name: "орехи", amount: "" },
  ],
  "Пиле с ориз - класическа рецепта": [
    { name: "пиле", amount: "1 бр. (~1.5 кг)" },
    { name: "лук", amount: "2 глави" },
    { name: "моркови", amount: "1 бр." },
    { name: "чушки", amount: "1 бр." },
    { name: "ориз", amount: "400 г" },
    { name: "олио", amount: "40 мл" },
    { name: "масло", amount: "40 г" },
    { name: "черен пипер", amount: "10 зърна" },
    { name: "сол", amount: "на вкус" },
  ],
};

// Pantry — loaded from Supabase's pantry_items table when configured,
// else kept in localStorage. Used to cross ingredients off the shopping list.
let pantryItems = []; // [{ id, name, amount }]
let pantryHouseholdId = null;

// ==========================================================================
// INGREDIENT CATEGORIZATION (simple keyword matching)
// ==========================================================================
const CATEGORIES = [
  {
    label: "ПЛОДОВЕ И ЗЕЛЕНЧУЦИ",
    keywords: ["домат", "краставиц", "лук", "чушк", "морков", "чесън", "магданоз",
      "копър", "зеле", "цвекло", "спанак", "картоф", "гъб", "грах", "царевиц",
      "маслин", "лимон"],
  },
  {
    label: "МЕСО И РИБА",
    keywords: ["шкембе", "кайма", "пържол", "пиле", "скумрия", "шунка"],
  },
  {
    label: "МЛЕЧНИ И ЯЙЦА",
    keywords: ["мляко", "сирене", "кашкавал", "краве масло", "яйц", "жълтъц"],
  },
  {
    label: "ЗЪРНЕНИ И ХЛЯБ",
    keywords: ["ориз", "брашно", "хляб", "фиде", "боб", "леща"],
  },
  {
    label: "ПОДПРАВКИ И ДРУГИ",
    keywords: [], // fallback bucket
  },
];

function categorize(name) {
  const lower = name.toLowerCase();
  for (const cat of CATEGORIES) {
    if (cat.keywords.some(k => lower.includes(k))) return cat.label;
  }
  return "ПОДПРАВКИ И ДРУГИ";
}

function isInPantry(name) {
  const lower = name.toLowerCase();
  return pantryItems.some(p => lower.includes(p.name.toLowerCase()));
}

// ==========================================================================
// MEAL PLAN (localStorage key: "mealPlan")
// ==========================================================================
const DAYS = [
  { key: 'Monday', label: 'Понеделник' },
  { key: 'Tuesday', label: 'Вторник' },
  { key: 'Wednesday', label: 'Сряда' },
  { key: 'Thursday', label: 'Четвъртък' },
  { key: 'Friday', label: 'Петък' },
  { key: 'Saturday', label: 'Събота' },
  { key: 'Sunday', label: 'Неделя' },
];

function loadMealPlan() {
  const raw = localStorage.getItem('mealPlan');
  if (raw) {
    try { return JSON.parse(raw); } catch (e) { /* fall through */ }
  }
  // First-run demo data: a real week built from the 21 gotvach.bg recipes
  // above, 3 dishes per day, so Shopping List has real data to consolidate.
  const demo = {
    Monday: ["Шкембе чорба с прясно мляко", "Шопска Салата Оригинал", "Класически пържени кюфтета"],
    Tuesday: ["Класическа мусака с картофи и кайма", "Класическа овчарска салата", "Пилешка супа с фиде и застройка"],
    Wednesday: ["Пълнени чушки с кайма, ориз и бял сос", "Класически Български Таратор", "Кебапчета"],
    Thursday: ["Боб яхнията на баба", "Свински пържоли на тиган", "Лек зеленчуков гювеч"],
    Friday: ["Вкусна леща яхния", "Пиле с картофи на фурна по селски", "Зелеви сарми с кайма и ориз"],
    Saturday: ["Перфектната скумрия на скара", "Салата Снежанка", "Ориз със спанак"],
    Sunday: ["Класическа зеле и моркови салата", "Салата от печено цвекло", "Пиле с ориз - класическа рецепта"],
  };
  localStorage.setItem('mealPlan', JSON.stringify(demo));
  return demo;
}

function saveMealPlanLocal(plan) {
  localStorage.setItem('mealPlan', JSON.stringify(plan));
}

// Full-replace sync: wipes this household's rows and re-inserts the current
// plan. Simple and fine at this scale (7 days x a few meals).
async function syncMealPlanToSupabase(plan) {
  if (!supabaseConfigured) return;
  try {
    const householdId = await getHouseholdId();
    const { error: delError } = await supabaseClient
      .from('meal_plan_entries')
      .delete()
      .eq('household_id', householdId);
    if (delError) throw delError;

    const rows = [];
    DAYS.forEach(day => {
      (plan[day.key] || []).forEach((mealName, i) => {
        rows.push({ household_id: householdId, day_key: day.key, meal_name: mealName, sort_order: i });
      });
    });
    if (rows.length > 0) {
      const { error: insError } = await supabaseClient.from('meal_plan_entries').insert(rows);
      if (insError) throw insError;
    }
  } catch (err) {
    console.error('Meal plan sync error:', err);
  }
}

function saveMealPlan(plan) {
  saveMealPlanLocal(plan);
  syncMealPlanToSupabase(plan);
}

async function loadMealPlanFromSupabase() {
  if (!supabaseConfigured) return null;
  try {
    const householdId = await getHouseholdId();
    const { data, error } = await supabaseClient
      .from('meal_plan_entries')
      .select('day_key, meal_name, sort_order')
      .eq('household_id', householdId)
      .order('sort_order', { ascending: true });
    if (error) throw error;

    const plan = {};
    DAYS.forEach(d => { plan[d.key] = []; });
    (data || []).forEach(row => {
      if (!plan[row.day_key]) plan[row.day_key] = [];
      plan[row.day_key].push(row.meal_name);
    });
    return plan;
  } catch (err) {
    console.error('Meal plan load error:', err);
    return null;
  }
}

let mealPlan = loadMealPlan();

function dateLabelForDay(index) {
  const base = new Date('2026-07-27T00:00:00');
  const d = new Date(base);
  d.setDate(base.getDate() + index);
  return d.toLocaleDateString('bg-BG', { day: 'numeric', month: 'short' });
}

function renderDayRail() {
  const rail = document.getElementById('dayRail');
  rail.innerHTML = '';

  DAYS.forEach((day, i) => {
    const meals = mealPlan[day.key] || [];

    const card = document.createElement('div');
    card.className = 'day-card';

    const header = document.createElement('div');
    header.className = 'day-card-header';
    header.innerHTML = `<span class="day-name">${day.label}</span><span class="day-date">${dateLabelForDay(i)}</span>`;
    card.appendChild(header);

    meals.forEach((meal, mealIndex) => {
      const row = document.createElement('div');
      row.className = 'meal-row';
      row.innerHTML = `<span>${escapeHtml(meal)}</span>`;
      const removeBtn = document.createElement('button');
      removeBtn.className = 'meal-remove';
      removeBtn.textContent = '✕';
      removeBtn.addEventListener('click', () => {
        mealPlan[day.key].splice(mealIndex, 1);
        saveMealPlan(mealPlan);
        renderDayRail();
        renderShoppingList();
      });
      row.appendChild(removeBtn);
      card.appendChild(row);
    });

    const addRow = document.createElement('div');
    addRow.className = 'add-meal-row';
    const input = document.createElement('input');
    input.className = 'add-meal-input';
    input.placeholder = 'Добави ястие…';
    const addBtn = document.createElement('button');
    addBtn.className = 'add-meal-btn';
    addBtn.textContent = '+';

    const addMeal = () => {
      const value = input.value.trim();
      if (!value) return;
      if (!mealPlan[day.key]) mealPlan[day.key] = [];
      mealPlan[day.key].push(value);
      saveMealPlan(mealPlan);
      renderDayRail();
      renderShoppingList();
    };

    addBtn.addEventListener('click', addMeal);
    input.addEventListener('keydown', (e) => {
      if (e.key === 'Enter') addMeal();
    });

    addRow.appendChild(input);
    addRow.appendChild(addBtn);
    card.appendChild(addRow);

    rail.appendChild(card);
  });
}

function escapeHtml(str) {
  const div = document.createElement('div');
  div.textContent = str;
  return div.innerHTML;
}

renderDayRail();

// ==========================================================================
// SHOPPING LIST — generated from the meal plan + RECIPES_DB
// ==========================================================================
function renderShoppingList() {
  const body = document.getElementById('receiptBody');
  if (!body) return;

  // 1. Collect every meal across the week, and every ingredient for meals
  //    that we actually have a recipe for.
  let mealCount = 0;
  const unmatchedMeals = [];
  const ingredientMap = {}; // key: lowercased name -> { displayName, amounts:Set, category }

  DAYS.forEach(day => {
    const meals = mealPlan[day.key] || [];
    meals.forEach(mealName => {
      mealCount++;
      const recipe = RECIPES_DB[mealName];
      if (!recipe) {
        unmatchedMeals.push(mealName);
        return;
      }
      recipe.forEach(ing => {
        const key = ing.name.toLowerCase().trim();
        if (!ingredientMap[key]) {
          ingredientMap[key] = {
            displayName: ing.name,
            amounts: new Set(),
            category: categorize(ing.name),
          };
        }
        ingredientMap[key].amounts.add(ing.amount ? ing.amount : "на вкус");
      });
    });
  });

  const allIngredients = Object.values(ingredientMap);
  const neededIngredients = allIngredients.filter(i => !isInPantry(i.displayName));
  const pantryMatched = allIngredients.filter(i => isInPantry(i.displayName));

  // 2. Empty state
  if (mealCount === 0) {
    body.innerHTML = `
      <div class="receipt-empty">
        Все още няма добавени ястия.<br>
        Добави няколко в таб „Меню", за да видиш списъка тук.
      </div>`;
    return;
  }

  // 3. Group needed ingredients by category, in fixed category order.
  const grouped = {};
  neededIngredients.forEach(ing => {
    if (!grouped[ing.category]) grouped[ing.category] = [];
    grouped[ing.category].push(ing);
  });

  let html = '';
  html += `<div class="receipt-title">СПИСЪК · ТАЗИ СЕДМИЦА</div>`;
  html += `<div class="receipt-sub">${mealCount} ястия · ${neededIngredients.length} продукта</div>`;

  CATEGORIES.forEach(cat => {
    const items = grouped[cat.label];
    if (!items || items.length === 0) return;
    html += `<div class="receipt-group">`;
    html += `<div class="receipt-cat">${cat.label}</div>`;
    items
      .sort((a, b) => a.displayName.localeCompare(b.displayName, 'bg'))
      .forEach(ing => {
        const amountText = Array.from(ing.amounts).join(' + ');
        html += `<div class="receipt-row"><span>${escapeHtml(capitalize(ing.displayName))}</span><span class="dots"></span><span class="amount">${escapeHtml(amountText)}</span></div>`;
      });
    html += `</div>`;
  });

  if (pantryMatched.length > 0) {
    html += `<div class="receipt-divider"></div>`;
    html += `<div class="receipt-group">`;
    html += `<div class="receipt-cat">ВЕЧЕ ИМАШ ВКЪЩИ</div>`;
    pantryMatched.forEach(ing => {
      html += `<div class="receipt-row have-at-home"><span>${escapeHtml(capitalize(ing.displayName))}</span><span class="dots"></span><span class="amount">—</span></div>`;
    });
    html += `</div>`;
    html += `<div class="receipt-note">${pantryMatched.length} продукта прескочени — вече ги имаш в Кухня</div>`;
  }

  if (unmatchedMeals.length > 0) {
    html += `<div class="receipt-divider"></div>`;
    html += `<div class="receipt-note">Няма рецепта за: ${unmatchedMeals.map(escapeHtml).join(', ')} — ръчно добавени ястия все още нямат съставки.</div>`;
  }

  body.innerHTML = html;
}

function capitalize(str) {
  return str.charAt(0).toUpperCase() + str.slice(1);
}

renderShoppingList();

// Try to load real data from Supabase (if configured); re-render whatever
// changed once it resolves. The app is already fully usable before this
// finishes — this only upgrades the data source underneath it.
loadRecipesFromSupabase().then(() => {
  renderDayRail();
  renderShoppingList();
});

// Same upgrade pattern for the meal plan: keep the localStorage version
// on screen immediately, then swap in Supabase's copy once it resolves.
// If Supabase is configured but has no rows yet (first connect), push the
// current (demo or local) plan up so it becomes the shared source of truth.
loadMealPlanFromSupabase().then(remotePlan => {
  if (!remotePlan) return;
  const hasRemoteData = Object.values(remotePlan).some(meals => meals.length > 0);
  if (hasRemoteData) {
    mealPlan = remotePlan;
    saveMealPlanLocal(mealPlan);
    renderDayRail();
    renderShoppingList();
  } else {
    syncMealPlanToSupabase(mealPlan);
  }
});

// ==========================================================================
// PANTRY: CRUD against Supabase's pantry_items table (falls back to
// localStorage when Supabase isn't configured, same pattern as the meal plan)
// ==========================================================================
function loadPantryFromLocalStorage() {
  const raw = localStorage.getItem('pantryItems');
  if (raw) {
    try { return JSON.parse(raw); } catch (e) { /* fall through */ }
  }
  const demo = [
    { id: 'demo-1', name: 'Ориз', amount: '1 кг' },
    { id: 'demo-2', name: 'Олио', amount: '500 мл' },
    { id: 'demo-3', name: 'Яйца', amount: '6 бр' },
  ];
  localStorage.setItem('pantryItems', JSON.stringify(demo));
  return demo;
}

function savePantryToLocalStorage() {
  localStorage.setItem('pantryItems', JSON.stringify(pantryItems));
}

async function getHouseholdId() {
  if (pantryHouseholdId) return pantryHouseholdId;
  const { data, error } = await supabaseClient.from('households').select('id').limit(1).single();
  if (error || !data) throw error || new Error('No household found');
  pantryHouseholdId = data.id;
  return pantryHouseholdId;
}

async function loadPantryItems() {
  if (!supabaseConfigured) {
    pantryItems = loadPantryFromLocalStorage();
    return;
  }
  try {
    const householdId = await getHouseholdId();
    const { data, error } = await supabaseClient
      .from('pantry_items')
      .select('id, ingredient_name, amount')
      .eq('household_id', householdId)
      .order('id', { ascending: true });
    if (error) throw error;
    pantryItems = (data || []).map(r => ({ id: r.id, name: r.ingredient_name, amount: r.amount || '' }));
  } catch (err) {
    console.error('Pantry load error:', err);
    pantryItems = loadPantryFromLocalStorage();
  }
}

async function addPantryItem(name, amount) {
  if (!name) return;
  if (supabaseConfigured) {
    try {
      const householdId = await getHouseholdId();
      const { data, error } = await supabaseClient
        .from('pantry_items')
        .insert({ household_id: householdId, ingredient_name: name, amount: amount || '' })
        .select('id, ingredient_name, amount')
        .single();
      if (error) throw error;
      pantryItems.push({ id: data.id, name: data.ingredient_name, amount: data.amount || '' });
    } catch (err) {
      console.error('Pantry add error:', err);
      alert('Грешка при добавяне в Supabase — виж конзолата.');
      return;
    }
  } else {
    pantryItems.push({ id: Date.now(), name, amount: amount || '' });
    savePantryToLocalStorage();
  }
  renderPantry();
  renderShoppingList();
}

async function deletePantryItem(id) {
  if (supabaseConfigured) {
    try {
      const { error } = await supabaseClient.from('pantry_items').delete().eq('id', id);
      if (error) throw error;
    } catch (err) {
      console.error('Pantry delete error:', err);
      alert('Грешка при изтриване от Supabase — виж конзолата.');
      return;
    }
  }
  pantryItems = pantryItems.filter(p => p.id !== id);
  if (!supabaseConfigured) savePantryToLocalStorage();
  renderPantry();
  renderShoppingList();
}

function renderPantry() {
  const grid = document.getElementById('pantryGrid');
  if (!grid) return;
  grid.innerHTML = '';

  pantryItems.forEach(item => {
    const slot = document.createElement('div');
    slot.className = 'pantry-slot filled';
    slot.innerHTML = `
      <button class="pantry-remove" aria-label="Премахни">✕</button>
      <span class="pantry-name">${escapeHtml(capitalize(item.name))}</span>
      <span class="pantry-qty">${escapeHtml(item.amount || '')}</span>
    `;
    slot.querySelector('.pantry-remove').addEventListener('click', () => deletePantryItem(item.id));
    grid.appendChild(slot);
  });

  const addSlot = document.createElement('div');
  addSlot.className = 'pantry-slot empty';
  addSlot.id = 'addPantrySlot';
  addSlot.innerHTML = `<span class="plus">+</span><span>Добави продукт</span>`;
  addSlot.addEventListener('click', showAddPantryForm);
  grid.appendChild(addSlot);
}

function showAddPantryForm() {
  const grid = document.getElementById('pantryGrid');
  const addSlot = document.getElementById('addPantrySlot');
  if (!grid || !addSlot) return;

  const formSlot = document.createElement('div');
  formSlot.className = 'pantry-slot pantry-slot-form';
  formSlot.innerHTML = `
    <input class="pantry-input" placeholder="Продукт" />
    <input class="pantry-input pantry-input-amount" placeholder="Количество (по желание)" />
    <div class="pantry-form-actions">
      <button class="pantry-form-cancel">Отказ</button>
      <button class="pantry-form-save">Запази</button>
    </div>
  `;
  grid.replaceChild(formSlot, addSlot);

  const nameInput = formSlot.querySelector('.pantry-input');
  const amountInput = formSlot.querySelector('.pantry-input-amount');
  nameInput.focus();

  const save = () => {
    const name = nameInput.value.trim();
    const amount = amountInput.value.trim();
    if (!name) return;
    addPantryItem(name, amount);
  };

  formSlot.querySelector('.pantry-form-save').addEventListener('click', save);
  formSlot.querySelector('.pantry-form-cancel').addEventListener('click', renderPantry);
  nameInput.addEventListener('keydown', e => { if (e.key === 'Enter') save(); });
  amountInput.addEventListener('keydown', e => { if (e.key === 'Enter') save(); });
}

loadPantryItems().then(() => {
  renderPantry();
  renderShoppingList();
});