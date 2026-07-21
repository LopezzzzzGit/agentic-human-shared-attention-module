# Recipe compiler instruction

Convert a privacy-filtered human desktop demonstration into a neutral semantic
recipe. Merge incidental input into intentional steps. Each step must include:

- an imperative intent;
- an ordered list of target addresses: UI Automation identity, visible text,
  relation to nearby elements, region, then raw coordinates only as last-resort
  evidence;
- the action and optional typed input;
- an observable expected result.

Never invent tool calls, provider-specific fields, hidden state, or passwords.
Return only JSON conforming to `recipeSchema`.
