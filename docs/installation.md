# Installation

This project expects a local secrets file at `solutionfiles/secrets/secret.json`. The file is already generated in this repo and ignored by git.

Example structure:
```json
{
  "LoginDb": {
    "Provider": "sqlserver",
    "ConnectionString": "Server=localhost,1433;Database=athena.net;User ID=sa;Password=...;Encrypt=True;TrustServerCertificate=True;"
  },
  "SqlServer": {
    "SaPassword": "..."
  }
}
```
