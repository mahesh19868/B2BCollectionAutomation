# B2B Collections Automation – MVP

A small web application that helps Indian distributors track outstanding B2B invoices, prioritise
payment follow-ups, record promises to pay, and send controlled, previewed email reminders.

It is a **collections workflow aid, not an accounting ledger**. Balances reflect the data imported
and the payments recorded here; refresh by re-importing your accounting export.

## Solution layout

| Path | Purpose |
|---|---|
| `src/Collections.Core` | Entities and pure, unit-tested business rules: balances, ageing, dashboard totals, allocation and promise rules, worklist ordering, CSV parsing/validation/import planning, reminder eligibility and templates, INR/date formatting. No ASP.NET or EF dependency. |
| `src/Collections.Web` | ASP.NET Core MVC app (single deployable): Identity sign-in, tenant-scoped EF Core `AppDbContext` + migrations, application services, email providers, controllers and Razor views. |
| `tests/Collections.UnitTests` | xUnit tests for Core rules (no database). |
| `tests/Collections.IntegrationTests` | xUnit tests running the real app (`WebApplicationFactory`) against a throwaway SQL Server database. |
| `samples/demo-invoices.csv` | Synthetic demo data for local use. |

## Prerequisites

- .NET SDK 10 (`dotnet --list-sdks`)
- SQL Server: LocalDB (installed with Visual Studio) or any SQL Server instance
- Restore the local EF tool once: `dotnet tool restore`

## Run locally

```powershell
dotnet tool restore
dotnet build
dotnet run --project src/Collections.Web --launch-profile https
```

Open https://localhost:7195, click **Create a workspace**, then **Import** → upload
`samples/demo-invoices.csv` → review → **Confirm import**.

In Development:
- the database `CollectionsDev` on `(localdb)\MSSQLLocalDB` is created/migrated automatically
  (`Database:MigrateOnStartup=true` in `appsettings.Development.json`);
- emails are **never sent**: the `Capture` provider writes each message to
  `src/Collections.Web/App_Data/outbox/*.txt` (git-ignored).

No demo users or passwords are seeded; you register your own test workspace.

## Tests

```powershell
dotnet test                                   # all tests
dotnet test tests/Collections.UnitTests       # fast, no database
```

Integration tests create a uniquely named database on `(localdb)\MSSQLLocalDB` and drop it afterwards.
To use another server set `COLLECTIONS_TEST_SQL`, e.g.
`$env:COLLECTIONS_TEST_SQL = "Server=localhost;Trusted_Connection=True;TrustServerCertificate=True"`
(the test adds its own `Database=` name).

## Database migrations

```powershell
# create a migration after changing the model
dotnet ef migrations add <Name> --project src/Collections.Web --output-dir Data/Migrations
# apply to the configured database
dotnet ef database update --project src/Collections.Web
# produce an idempotent SQL script for production deployment
dotnet ef migrations script --idempotent --project src/Collections.Web -o migrate.sql
```

In production keep `Database:MigrateOnStartup=false` and apply the reviewed script during deployment.

## Configuration

All settings can be supplied as environment variables (`__` separates sections) or user secrets
(`dotnet user-secrets --project src/Collections.Web set "Email:Smtp:Password" "..."`).
**Never put credentials in `appsettings*.json`.**

| Setting | Environment variable | Default / notes |
|---|---|---|
| Connection string | `ConnectionStrings__DefaultConnection` | Required. LocalDB in Development. |
| Migrate on startup | `Database__MigrateOnStartup` | `false` (Development: `true`). |
| Email provider | `Email__Provider` | `Capture` (local files, never sends) or `Smtp`. **Required outside Development** – the app refuses to start without it. |
| Sender address | `Email__FromAddress` | Envelope sender, e.g. `reminders@yourdomain.in`. Distributor name is the display name; distributor contact email is Reply-To. |
| SMTP | `Email__Smtp__Host`, `__Port` (587), `__Username`, `__Password`, `__UseStartTls` (true) | Any SMTP relay (SES, SendGrid SMTP, Zoho, Gmail Workspace…). |
| Capture folder | `Email__CaptureDirectory` | `App_Data/outbox` |
| Import limits | `Import__MaxFileBytes`, `Import__MaxRows` | 2 MB, 5000 rows |
| Rate limits | `RateLimits__LoginPerMinute`, `RateLimits__ExpensivePerMinute` | 10 per IP; 30 per business (import/send) |
| Data-protection keys | `DataProtection__KeysDirectory` | Set in production to a persistent, private folder so sign-in cookies survive restarts. |
| Environment | `ASPNETCORE_ENVIRONMENT` | `Production` unless set. |
| Public URL | `ASPNETCORE_URLS` / reverse proxy | Serve over HTTPS only (cookies are `Secure`). |

Per-business settings (name, contact email/phone, reminder cooldown, subject/body template) are edited
in the app under **Settings**. The business time zone is `Asia/Kolkata`.

## Business rules (single source of truth: `Collections.Core`)

- **Outstanding** = invoice amount − sum of allocated payments. Negative results are not clamped:
  the invoice is flagged **Data error**, excluded from totals and shown on the dashboard.
- **Status** (business-local date, `Asia/Kolkata`): *Not due* (due date after today), *Due today*,
  *Overdue* (due date before today), *Paid* (outstanding 0). *Partially paid* = outstanding > 0 with allocations.
- **Ageing**: Not due/due today · 1–30 · 31–60 · 61+ calendar days overdue. Paid invoices excluded.
- **Dashboard**: Total outstanding = all positive balances; Overdue = due date < today; Due in next 7 days =
  due today through today+7 inclusive; Collections this month = payments dated in the current
  business-local calendar month; Missed promises = open promises dated before today (system-calculated,
  not a user-confirmed outcome).
- **Payments** are entered manually and allocated to the customer's invoices. An allocation cannot exceed the
  invoice's outstanding; total allocations cannot exceed the payment. Remainders stay visible as *unallocated*.
  Payments with the same reference for the same customer are rejected. A payment entered in error can be
  deleted; the deletion is recorded in the customer history.
- **Promises to pay** never change balances. Only a user marks a promise Kept/Missed/Cancelled, once.
- **Worklist order**: (1) open promise date passed or > 60 days overdue, (2) other overdue,
  (3) due today/upcoming; then most days overdue, larger balance, invoice number.

## CSV import

Template: **Import → Download CSV template**. Columns `CustomerCode` (optional), `CustomerName`,
`CustomerEmail` (optional), `InvoiceNumber`, `InvoiceDate`, `DueDate`, `InvoiceAmount`.

- Dates: `YYYY-MM-DD`, `DD-MM-YYYY`, `DD/MM/YYYY`, `DD-Mon-YYYY` (numeric dates are day-first).
- Amounts: digits, optional `.` and up to 2 decimals, commas allowed (`1,25,000.00`), no currency symbol, > 0.
- UTF-8 CSV only (Excel: *CSV UTF-8 (Comma delimited)*). `.xlsx` is not supported yet.
- Due date cannot be before invoice date.

Duplicate policy: an invoice is identified by **invoice number within the business** (case-insensitive).
Repeated numbers inside a file are all rejected; identical existing invoices are skipped (re-import is safe);
changed existing invoices are updated **only when the user ticks "apply updates"**; a number belonging to a
different customer is rejected; existing customer name/email are never changed by import.
Customers are matched by `CustomerCode`, otherwise by exact name (ambiguous names are rejected).

Flow: upload → stored, validated preview (the file itself is not kept) → confirm → one database transaction
(re-validated). If anything fails, nothing is saved and the batch is marked failed. Previews expire after 24 h.
Rejected rows can be downloaded as a CSV (values formula-escaped). Imports never send reminders.

## Email reminders

Manual only: open an invoice → **Preview reminder** → **Send**. Automated/scheduled reminders are not
implemented (no background scheduler is included). A reminder is blocked when the customer has no valid email,
is inactive or paused, the invoice is paused, disputed, paid or has a data error, a reminder for the invoice
was sent within the cooldown, or the business has no contact email. Every attempt is logged (Sent/Failed with a
sanitised error); a per-form idempotency key prevents double sends; failed reminders can be retried once the
cause is fixed if the balance and recipient are unchanged.

## Security notes

- ASP.NET Core Identity (PBKDF2 password hashing, lockout after 5 failures, 8-hour sliding session).
- Tenant isolation: workspace id comes only from the signed-in user's claim; EF global query filters on every
  business-owned table; `SaveChanges` refuses cross-tenant writes; integration tests cover cross-tenant reads/writes.
- Global antiforgery validation, Razor output encoding, CSP without inline script, `Secure`/`HttpOnly` cookies,
  rate limiting on sign-in, import and send.

## Before using real customer data

- Configure backups for the SQL Server database and **perform a test restore**; document it.
- Set `DataProtection__KeysDirectory`, HTTPS, an SMTP provider with SPF/DKIM for the sender domain.
- Review applicable Indian privacy (DPDP Act) and email communication requirements. No compliance is claimed.
