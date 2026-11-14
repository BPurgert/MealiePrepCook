# 🍳 MealieCategory

Turn messy, free-form recipe ingredients in [Mealie](https://mealie.io) into tidy **foods**, **units**, and **quantities**, then sort those foods into shopping-list **labels**. 🛒✨

"2 cups chopped onion" becomes `2` + `cup` + `onion` + note `chopped`, and onions land in your *Produce* aisle. 🧅

## 🚨 BACK UP MEALIE FIRST! 🚨

This app **edits your recipes and foods**. Take a Mealie backup (Admin → Backups) before running any `--apply` command. 💾 Nothing is written until you approve it, but a backup is cheap and regret is not. 😅

## 🚀 Quick start

You need the .NET 10 SDK, a Mealie API token, and an OpenAI API key.

```powershell
$env:MEALIE_URL       = "https://your-mealie.example.com"
$env:MEALIE_API_TOKEN = "your-mealie-token"
$env:OPENAI_API_KEY   = "your-openai-key"
$env:OPENAI_MODEL     = "gpt-6-luna"   # optional; this is the default

$app = ".\MealieCategory\MealieCategory.csproj"

dotnet run --project $app                    # 👀 look around (read-only)
dotnet run --project $app -- --propose       # 🤖 AI proposes ingredient conversions
# ✍️ edit mealie-conversion-review.json: set "approved": true on the ones you like
dotnet run --project $app -- --apply         # ✅ apply approved conversions

dotnet run --project $app -- --propose-labels  # 🏷️ AI proposes labels for foods
# ✍️ edit mealie-label-review.json and approve
dotnet run --project $app -- --apply-labels    # ✅ apply approved labels
```

## 🧰 What each command does

| Command | What happens | Changes Mealie? |
|---|---|---|
| *(none)* | Lists labels, every recipe's ingredients (food + unit vs. free-form), counts per label, foods, and units | No |
| `--propose` | Sends free-form ingredients to OpenAI and saves suggestions to `mealie-conversion-review.json` | No |
| `--apply` | Applies the entries you approved (creating new foods/units if needed) | **Yes** ⚠️ |
| `--propose-labels` | Asks OpenAI to pick a label for each food that has none, saved to `mealie-label-review.json` | No |
| `--apply-labels` | Applies the approved labels | **Yes** ⚠️ |

## 🛡️ Safety nets

- ✅ **Nothing happens without approval.** Every suggestion starts as `"approved": false`.
- 🚫 Entries with a `skipReason` are reported and skipped, even if approved.
- 🕵️ Recipes changed since the proposal was made are refused.
- 🔁 `--apply-labels` is safe to re-run; foods that already have a label are never overwritten.
- 💸 **Batching + checkpoints:** ingredients go to OpenAI in batches of 40 (labels: 100), with duplicates removed. If `--propose` is interrupted, re-run it to resume from `mealie-conversion-checkpoint.json`.
- 👁️ After `--apply`, each updated recipe has Mealie's "disable ingredient amounts" setting turned off. Otherwise Mealie would show structured ingredients as blank. 🙈

## 🔒 Privacy

OpenAI sees ingredient text, plus your food, unit, and label names. It never sees recipe names or your Mealie token. Requests are sent with storage disabled. Review and checkpoint files contain recipe data in plain text, and they, along with `/private/` and launcher scripts like `run-mealie.ps1`, are Git-ignored. Keep your keys out of source control! 🔑

## 🧪 Tests

```powershell
dotnet test .\MealieCategory.Tests\MealieCategory.Tests.csproj
```

Built and checked against Mealie v2.8.0. Other versions may behave differently, which is one more reason to back up first. 😉
