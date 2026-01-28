# Data and tools migration prompt

Goal
- Define how legacy data and tools feed the C# servers.

Legacy references
- upstream/db/ (YAML/TXT data)
- upstream/npc/ (scripts)
- upstream/sql-files/ (main.sql, logs.sql, upgrades)
- upstream/src/tool/ (yaml2sql, mapcache, csv2yaml)

Key decisions
- Keep legacy YAML/TXT and parse in C#, or convert to SQL and load into DB.
- Map cache generation: reuse legacy tool or port to C#.
- NPC script support: interpreter vs. compatibility layer.

Next tasks
- Inventory required data formats for MVP map server.
- Decide whether to embed legacy tools or reimplement in C#.
- Define a repeatable import pipeline for DB and NPC assets.

Definition of done
- A reproducible pipeline creates DB + data assets for C# servers.

Cleanup notes
- Trim this file as decisions are locked in.
