---
name: demo-script
description: Produce or refresh docs/DEMO.md (2-minute script with fallbacks, judge try-it protocol, demo-video shot list, Devpost text) via the demo-director agent.
context: fork
agent: demo-director
background: false
disable-model-invocation: true
---
Write or refresh docs/DEMO.md following your agent instructions. Use only features verified to L5 in docs/STATUS.md; mark the
rest "if time". Then list the 5 riskiest moments in the script with their fallbacks.
