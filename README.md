# Mealie recipe ingredients

This console app lists ingredient entries from each recipe accessible to your
Mealie account, preceded by all labels available to your Mealie group. It
fetches every page of labels and recipes and then fetches each recipe's
details. Ingredients come from recipes, not the global foods list. Repeated
ingredients are shown for each recipe, and free-text entries are retained.
Each entry also shows the label assigned to its linked food in Mealie. Entries
without a linked food or food label show `Label: none`; if Mealie returns only
a label ID, the ID is shown instead of a label name. Labels on individual
shopping list items are not included.

After the recipes, the app counts ingredient entries by food label, including
unused labels and `[none]` for entries without a label. Repeated ingredients
in different recipes are counted each time they appear. If only a label ID is
available, it is matched to the fetched labels when possible.
The app then lists all foods accessible to the account, including foods not
used by any recipe, followed by all accessible units.

Every recipe entry shows a type, with totals at the end of the output. Food
and unit categories depend on whether Mealie returned those objects, even if
an entry also has free-text notes. Referenced recipes take precedence. Entries
without food or unit are classified as having a quantity, free form, section
heading only, or empty; a section title can also accompany a normal ingredient.
Counts include each occurrence across recipes.

Create a long-lived API token in Mealie under **Profile > API Tokens**. Set the
instance URL and token as environment variables, then run:

```powershell
$env:MEALIE_URL = "https://your-mealie-instance"
$env:MEALIE_API_TOKEN = "<your-api-token>"
dotnet run --project .\MealieCategory\MealieCategory.csproj
```

Alternatively, edit the local `run-mealie.ps1` launcher to set your URL and
token, then run `.\run-mealie.ps1`. The launcher is ignored by Git and stores
the token in plaintext on your computer; keep the file private. It restores any
previous environment variable values after the app exits.

The URL may include a deployment path, such as `https://example.com/mealie`.
Use HTTPS when possible to protect the token. Do not commit the token or put it
in a shared launch configuration. The app exits with a nonzero status if it
cannot retrieve the complete list; output printed before an error is partial.

## Review AI ingredient conversions

The optional conversion workflow uses the Microsoft Agent Framework and the
OpenAI Responses API. It proposes conversions for free-form entries and
partially structured ingredients; fully structured ingredients, section
headings, and referenced recipes are left alone. Review the results carefully:
AI can misunderstand quantities, food names, and preparation details.

1. Set `MEALIE_URL` and `MEALIE_API_TOKEN` as above, plus an OpenAI API key:

   ```powershell
   $env:OPENAI_API_KEY = "<your-openai-api-key>"
   # Optional: default is gpt-6-luna, if available to your OpenAI account.
   $env:OPENAI_MODEL = "gpt-6-luna"
   dotnet run --project .\MealieCategory\MealieCategory.csproj -- --propose
   ```

   Progress is written to standard error while recipes are fetched and AI
   batches are processed, including periodic updates during a long AI request.
   Completed batches are saved to `mealie-conversion-checkpoint.json` in the
   current directory. If a run fails, rerun the same command to reuse matching
   batches; progress messages identify resumed batches.

2. Open `mealie-conversion-review.json` in the current directory. Each proposal
   starts with `"approved": false`. Check the original ingredient, food,
   unit, quantity, and note; set `"approved": true` only for changes you want.
   Existing catalog choices have IDs and names; proposed new entries use
   `newFoodName` or `newUnitName`. Skipped entries cannot be approved.
3. Apply the approved entries using the same Mealie URL:

   ```powershell
   dotnet run --project .\MealieCategory\MealieCategory.csproj -- --apply
   ```

Proposal generation does not modify Mealie. The review file contains recipe
data in plaintext, including the original ingredient arrays; keep it private.
Git ignores files with this default name, but copies or renamed files may not
be ignored. Keep copies in the Git-ignored `private` folder at the repository
root; create the folder if it does not exist. The app will not overwrite an
existing review file. Reviews made
before the disabled-quantity fix cannot be applied: move the old file to a
private location outside the repository, then generate a fresh review. Check
the new proposals before approving them; regeneration sends the inputs to
OpenAI again and incurs API charges.

The Git-ignored checkpoint contains AI results in plaintext; keep it private.
It is removed after the review file is successfully saved; an ignored `.lock`
file remains to prevent simultaneous proposal runs. Reuse requires the
same Mealie URL, model, conversion instructions, catalog IDs and details, and
batch inputs. Changed or reordered data may require new requests. A request
that fails before its result is saved may still have been processed and billed.
Runs made before checkpointing was added cannot be resumed. To discard an
unfinished checkpoint and request fresh results, remove the checkpoint file
while the app is not running. If saving the review file fails, move or remove
the incomplete review file before retrying; the checkpoint is retained.

The proposal's `original` is the ingredient text without the reporting label.
Mealie's hidden default quantity of one on a free-form entry is not treated as
an actual amount; the proposal can instead extract an amount from the text.

For each request, OpenAI receives the complete accessible food and unit names
(including unit abbreviations) and up to 40 distinct ingredient inputs,
including their original text, display, notes, quantities, and existing
food/unit codes. Recipe names and the Mealie token are not sent. Identical
inputs are deduplicated across recipes. Catalogs are repeated in each
synchronous request; no fixed cost limit is enforced. Requests specify
`store: false`, but consult OpenAI's current data-retention policy for other
applicable retention. You are responsible for API charges; check
[OpenAI pricing](https://openai.com/api/pricing/) and model availability.
`gpt-6-luna` is a cost-oriented starting point, not a guarantee of accuracy;
try another Responses API model with structured output using `OPENAI_MODEL`
if needed.

Applying can create approved foods and units, or reuse an existing exact-name
match, before updating recipes. New foods have no assigned shopping-list label
and new units use Mealie defaults; adjust them in Mealie if needed. The app
checks that all selected recipe ingredients still match the review file and
checks again immediately before each update. This is not an atomic
conditional write: a concurrent edit between the last check and the update
could still be overwritten. Avoid editing those recipes during application.
Failures can leave created catalog items or already updated recipes in place.
Check Mealie and regenerate a fresh review file before retrying; applying the
old file again will reject recipes that have changed.

Run the client tests with `dotnet test .\MealieCategory.Tests\MealieCategory.Tests.csproj`.

## Assign labels to foods

Labels group the shopping list. After ingredients are converted to foods, the
AI can pick a label for each food that has none:

1. Generate proposals (needs `OPENAI_API_KEY`; uses `OPENAI_MODEL`, default
   `gpt-6-luna`). This does not modify Mealie:

   ```powershell
   dotnet run --project .\MealieCategory\MealieCategory.csproj -- --propose-labels
   ```

2. Open `mealie-label-review.json` (Git-ignored; the app will not overwrite it).
   Each food starts with `"approved": false`. Check the suggested label and set
   `"approved": true` for the ones you want. Entries with a `skipReason` had no
   suitable label; approved ones are reported and skipped. To pick a label
   yourself, copy its `labelId` and `labelName` exactly from your Mealie labels.
3. Apply:

   ```powershell
   dotnet run --project .\MealieCategory\MealieCategory.csproj -- --apply-labels
   ```

Only foods without a label are proposed, and existing labels are never
overwritten. Foods are sent to OpenAI in batches of 100 along with your label
names; recipes and your Mealie token are not sent. Interrupted runs have no
checkpoint (batches are small); re-run `--propose-labels` after moving the
review file. Re-running `--apply-labels` is safe because foods already labeled
are skipped.

## Ingredient display after conversion

Mealie shows only an ingredient's note while a recipe's "disable ingredient
amounts" setting is on, so converted ingredients would look blank. `--apply`
therefore turns that setting off for every recipe it updates, and clears the
hidden default quantity of 1 on that recipe's unconverted free-form entries so
it does not appear in front of their text. Recipes you did not approve are
not touched.
