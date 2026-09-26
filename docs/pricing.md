# Price catalog and estimate limits

The bundled catalog is a fixed snapshot identified as `2026-09-22-current-price-fallback-v2`. It is not a live price feed. Prices are expressed in USD per million tokens for input, cached input and output.

Supported model names are listed in `SqliteHistoryCatalog.cs` and the CSV sample. Models without a matching entry are marked unknown; a mixture of priced and unpriced usage is marked partial.

## Override with your own CSV

Use the header:

```csv
model,effective_date,input_per_million,cached_per_million,output_per_million
```

See [the example CSV](../samples/prices.example.csv). A matching model uses the latest entry whose effective date does not lie after the usage timestamp. Importing the same model/date updates that entry. The built-in catalog does not overwrite imported prices.

The technical fallback date is `1970-01-01`, allowing older supported sessions to receive the snapshot's approximate equivalent. That date does not claim the model or rate existed in 1970, and the result does not reconstruct historical billing.

Cached input is a subset of input tokens, and reasoning is a subset of output tokens; neither is added again to total usage. Missing or incomplete log records can still produce incomplete estimates.

**The result is not the user's Codex subscription bill, account balance, historical invoice or measured spend.** Recheck provider prices before using an estimate to make a financial decision. This app intentionally keeps the catalog local.
