# AGENTS.md --- B2B Collections Automation MVP

## 1. Mission and product context

Build a small, secure web application that helps Indian distributors
track outstanding B2B invoices, prioritise payment follow-ups, record
promises to pay, and send controlled email reminders.

The first version is an operational tool for a distributor's owner or
accounts team---not a full accounting system, ERP, payment gateway, or
AI chatbot.

### Target customer

Indian distributors that: - Sell goods to other businesses on credit. -
Have dozens or hundreds of active business customers. - Currently track
dues and follow-ups in Excel, accounting exports, email, or WhatsApp. -
Need a reliable daily list of whom to contact and which payment promises
have been missed.

### Product outcome

A user should be able to answer these questions quickly: 1. How much
money is outstanding? 2. How much is overdue, and for how long? 3. Which
customers should we follow up with today? 4. What did a customer promise
to pay, and when? 5. Which promises have been missed? 6. What reminders
were sent, and to whom?

### MVP principles

-   **Accuracy before automation.** Financial calculations must be
    deterministic and tested.
-   **Import first.** Start with Excel/CSV exports; do not require an
    accounting-system integration.
-   **Human control.** Users can preview imports, correct data, and
    review reminders before sending.
-   **Small and maintainable.** Prefer a modular monolith and
    straightforward code over microservices or elaborate abstractions.
-   **No AI dependency.** The core MVP must work without an AI API.
-   **Validate before expanding.** Build only what supports a pilot with
    real distributors. Do not add speculative features without a clear
    user need.

## 2. Default technology choices

Use these defaults unless the repository already has a working,
intentional equivalent. Inspect the repository before changing its
structure.

-   **Backend:** ASP.NET Core on the current supported LTS .NET version
    available in the development environment (prefer .NET 10 LTS when
    available).
-   **Language:** C# with nullable reference types enabled.
-   **Web UI:** ASP.NET Core MVC with Razor Views and a small amount of
    vanilla JavaScript. Keep the UI responsive and simple.
-   **Database:** SQL Server, using Entity Framework Core and
    migrations.
-   **Authentication:** ASP.NET Core Identity or an equivalent
    well-supported built-in authentication mechanism.
-   **Background work:** Use a durable scheduler such as Hangfire only
    when scheduled reminder delivery is implemented. Do not introduce a
    scheduler merely for its own sake.
-   **Email:** Use an email-provider abstraction and a development/test
    implementation. Do not hard-code provider credentials or tie
    business logic directly to a vendor SDK.
-   **Testing:** xUnit for unit/integration tests; use the test
    framework already present if the repository has one.
-   **Deployment:** Keep the application deployable as one web
    application plus one relational database. Document environment
    variables and migration steps.

Do not introduce React, Angular, microservices, event streaming,
Kubernetes, a mobile application, a multi-agent framework, or an AI
service for the MVP unless the user explicitly requests it and there is
a concrete requirement.

## 3. How coding agents must work

### Before changing code

1.  Inspect the repository, existing conventions, solution/project
    files, and current tests.
2.  Identify the smallest useful implementation step.
3.  Explain briefly which files will change and why when the task is
    non-trivial.
4.  Preserve existing working code and conventions unless there is a
    clear reason to change them.
5.  Do not assume that a feature exists because it is described in this
    file; verify the current code.

### During implementation

-   Work in small, reviewable increments.
-   Make one coherent change at a time; avoid rewriting the entire
    application for a small feature.
-   Prefer readable, explicit C# over clever abstractions.
-   Keep controllers thin. Put business rules in application/domain
    services that can be unit-tested without HTTP or a database.
-   Use dependency injection for services and external integrations.
-   Use asynchronous APIs for database and network I/O; pass
    `CancellationToken` where appropriate.
-   Use EF Core parameterisation; never build SQL by concatenating user
    input.
-   Validate input on the server, even if the UI also validates it.
-   Use decimal types for money; never use `float` or `double` for
    financial values.
-   Use `DateOnly` for invoice/due/promise dates where time-of-day is
    irrelevant. Store audit timestamps as UTC `DateTimeOffset`.
-   Add or update tests with every business-rule change.
-   Add database migrations for schema changes; do not rely on
    `EnsureCreated()` for normal development or production.
-   Do not silently discard invalid rows, alter customer data, or send
    emails during an import.
-   Do not claim that a build or test passed unless it was actually run
    and its result observed.

### After implementation

1.  Build the solution.
2.  Run relevant tests.
3.  Fix regressions caused by the change.
4.  Summarise the behaviour implemented, key files changed,
    migration/configuration steps, and tests actually run.
5.  Call out anything not implemented or not verified.
6.  Never commit secrets, local database files, real customer data, or
    provider credentials.

If a task is too large, implement a safe vertical slice and list the
remaining work rather than producing a broad, untested scaffold.

## 4. MVP scope

### P0 --- Required for a useful pilot

1.  Secure sign-in and business/tenant isolation.
2.  Customer records and invoice records.
3.  CSV/Excel import with preview, validation, error reporting, and
    duplicate detection.
4.  Dashboard showing outstanding, overdue, and upcoming dues.
5.  Invoice ageing buckets.
6.  Daily follow-up worklist.
7.  Promise-to-pay recording and missed-promise indicators.
8.  Manual follow-up notes/history.
9.  Email reminders with templates, preview/approval, pause controls,
    and a delivery audit log.
10. Basic settings, validation messages, and useful empty/error states.
11. Automated tests for financial calculations, ageing, tenant
    isolation, imports, and reminder eligibility.

### P1 --- Add only after P0 is stable and piloted

-   Customer statement export.
-   Payment import/reconciliation from bank or accounting exports.
-   Team assignment and more granular roles.
-   Credit limits.
-   WhatsApp Business integration, including consent, approved
    templates, provider costs, and delivery status.
-   Tally or other accounting integrations.

### Explicitly out of scope for the first release

-   Creating GST invoices or e-invoice IRNs.
-   Accounting ledger, tax filing, or a replacement for Tally/ERP.
-   Payment processing, UPI collection links, or holding funds.
-   Automatic bank access or scraping.
-   WhatsApp automation.
-   AI-based credit scoring or autonomous customer messaging.
-   Multi-branch workflows, complex approval matrices, and custom report
    builders.
-   Native mobile apps and microservices.

Do not implement P1 or out-of-scope items unless asked.

## 5. Core user journeys

### Journey A: Set up a business

1.  A user registers/signs in.
2.  A business workspace is created or selected.
3.  All customer, invoice, import, follow-up, promise, and reminder data
    belongs to that workspace.
4.  A user can only access data belonging to their authorised workspace.

For the MVP, one account may belong to one workspace. Keep the data
model extensible enough to support multiple users in a workspace, but do
not build complex organisation administration yet.

### Journey B: Import invoices

1.  User downloads a CSV template.
2.  User uploads a CSV file or supported Excel file.
3.  Application validates the file type, size, headers, row count, and
    values.
4.  Application displays a preview and a row-by-row validation report.
5.  User confirms the import.
6.  Valid rows are committed in a transaction or in safe batches with
    clear results.
7.  Import summary shows created, updated, skipped, and failed rows.
8.  User can download or view error details and correct the source file.

Never send reminders as a side effect of importing data.

### Journey C: Review collections

1.  User opens the dashboard.
2.  User sees total outstanding, overdue amount, amount due soon, and
    missed promises.
3.  User opens the follow-up worklist.
4.  User filters by overdue status, age, amount, customer, and promise
    date.
5.  User records an action, note, next follow-up date, or promise to
    pay.

### Journey D: Send an email reminder

1.  User opens an eligible invoice/customer.
2.  Application renders a reminder preview with customer, invoice,
    amount due, and due date.
3.  User reviews and sends it, or enables a clearly configured reminder
    schedule.
4.  Application records the attempt and provider result.
5.  Failed deliveries are visible and can be retried safely.
6.  Disputed invoices, opted-out/invalid email addresses, and paused
    customers are not sent automated reminders.

Automated sending must be disabled by default until the user explicitly
configures and enables it.

## 6. Data model and relationships

Use normalised relational tables with primary keys, foreign keys,
indexes, and UTC audit timestamps. Names may be adjusted to match the
repository, but preserve the underlying business rules.

### Business / Workspace

Suggested fields: - `Id` - `Name` - `CreatedAtUtc` - `UpdatedAtUtc`

Every business-owned record must have a `BusinessId` (or equivalent
tenant key) or be related to a record that has one. Enforce tenant
scoping in the server-side data access layer; never trust a
client-supplied `BusinessId`.

### Application user / membership

Use the chosen authentication framework. Store the workspace association
or membership in a way that can be checked on every request. Do not
store passwords directly.

### Customer

Suggested fields: - `Id` - `BusinessId` - `CustomerCode` (optional) -
`Name` - `Email` (optional) - `Phone` (optional; do not use for WhatsApp
in MVP) - `IsActive` - `RemindersPaused` - `Notes` (optional) -
`CreatedAtUtc` - `UpdatedAtUtc`

Recommended uniqueness: customer code is unique within a business when
supplied. Do not assume customer names are unique.

### Invoice

Suggested fields: - `Id` - `BusinessId` - `CustomerId` -
`InvoiceNumber` - `InvoiceDate` (`DateOnly`) - `DueDate` (`DateOnly`) -
`CurrencyCode` (default `INR`; keep money handling explicit) -
`InvoiceAmount` (`decimal`) - `IsDisputed` - `RemindersPaused` -
`ExternalReference` (optional) - `CreatedAtUtc` - `UpdatedAtUtc`

Recommended uniqueness: `(BusinessId, CustomerId, InvoiceNumber)`,
subject to the accounting export's actual invoice-number semantics.
Confirm before adopting a different key. Invoice amount must be greater
than zero unless a documented credit-note/adjustment feature is later
introduced.

### Payment

Suggested fields: - `Id` - `BusinessId` - `CustomerId` - `PaymentDate`
(`DateOnly`) - `Amount` (`decimal`) - `Reference` (optional) - `Notes`
(optional) - `CreatedAtUtc` - `CreatedByUserId`

### Payment allocation

Suggested fields: - `Id` - `BusinessId` - `PaymentId` - `InvoiceId` -
`AllocatedAmount` (`decimal`)

A payment can be allocated to one or more invoices. The sum allocated to
an invoice cannot exceed its invoice amount less existing allocations,
unless a specifically designed overpayment workflow is introduced. The
sum of allocations cannot exceed the payment amount. Unallocated payment
balance may be supported as a later refinement; do not silently lose it.

If implementing a simpler MVP first, it is acceptable to record a single
paid-to-date amount per invoice only if the limitation is explicit and
the design does not pretend to support payment history or
reconciliation. Prefer the normalised payment/allocation model when
practical.

### Import batch and import row result

Store: - Business/workspace ID - Original filename (sanitised) - Import
type - Uploaded by - Upload and completion timestamps - Status - Counts
of accepted/rejected/created/updated/skipped rows - Row number,
validation errors, and outcome for each row (or a safe downloadable
error report)

Do not store uploaded files indefinitely by default. If a file is
temporarily stored, restrict access and define a cleanup policy.

### Follow-up activity

Suggested fields: - `Id` - `BusinessId` - `CustomerId` - `InvoiceId`
(optional) - `ActivityType` (call, email, note, other) - `Outcome`
(optional) - `Notes` - `OccurredAtUtc` - `NextFollowUpDate` (optional) -
`CreatedByUserId` - `CreatedAtUtc`

Follow-up notes must be treated as untrusted user content and
output-encoded in the UI.

### Promise to pay

Suggested fields: - `Id` - `BusinessId` - `CustomerId` - `InvoiceId`
(optional, if promise is at customer-account level) - `PromisedAmount`
(`decimal`) - `PromisedDate` (`DateOnly`) - `Status` (Open, Kept,
Missed, Cancelled) - `Notes` (optional) - `CreatedAtUtc` -
`CreatedByUserId` - `ResolvedAtUtc` (optional)

Avoid marking a promise as kept solely because the date has passed. A
promise is kept only when matching payments or a user-confirmed outcome
satisfies the promised amount. For the first version, provide an
explicit user action to mark a promise as kept or missed, and make the
rule visible.

### Reminder log

Suggested fields: - `Id` - `BusinessId` - `CustomerId` - `InvoiceId`
(optional) - `Channel` (Email only in MVP) - `TemplateKey` -
`Recipient` - `Subject` - `Status` (Queued, Sent, Failed, Cancelled) -
`ProviderMessageId` (optional) - `AttemptCount` - `LastError`
(sanitised; never include credentials) - `CreatedAtUtc` - `SentAtUtc`
(optional) - `CreatedByUserId` (optional for system-generated sends)

Store enough information to audit what was sent, but do not log
unnecessary personal or sensitive information.

## 7. Financial rules --- single source of truth

Implement financial calculations in testable services. Views and
controllers must not independently calculate balances.

### Invoice balance

For an invoice:

`Outstanding = InvoiceAmount - Sum(Allocated payments)`

-   Do not allow a negative outstanding balance under the standard
    allocation workflow.
-   Clamp neither bad data nor a negative result silently. Detect and
    report invalid data.
-   Use `decimal` and a consistent precision/scale in the database.
-   Store INR amounts to two decimal places unless the business
    explicitly requires a different precision.

### Status and due dates

Use the business's local calendar date for due/overdue decisions. Store
timestamps in UTC, but do not convert a `DateOnly` due date into a UTC
timestamp and accidentally shift its date.

For a supplied `asOfDate`: - **Not due:** outstanding \> 0 and due date
is after `asOfDate`. - **Due today:** outstanding \> 0 and due date
equals `asOfDate`. - **Overdue:** outstanding \> 0 and due date is
before `asOfDate`. - **Paid:** outstanding equals zero. - **Partially
paid:** outstanding \> 0 and at least one payment has been allocated.

Due-today invoices should be visible separately or clearly included in
the due-today/upcoming category. Do not call them overdue before the due
date has passed.

### Ageing buckets

Use the number of calendar days overdue:

-   Not due / due today: `DueDate >= asOfDate`
-   1--30 days overdue
-   31--60 days overdue
-   61+ days overdue

Only include invoices with a positive outstanding balance in
outstanding/ageing totals. Use the same shared calculation service for
dashboard, list, exports, and tests.

### Dashboard definitions

Use explicit, documented definitions: - **Total outstanding:** sum of
all positive invoice balances. - **Overdue amount:** sum of balances
where `DueDate < asOfDate`. - **Due in next 7 days:** sum of balances
with due dates from `asOfDate` through `asOfDate + 7 days`, inclusive,
excluding invoices already overdue. - **Collections this month:** sum of
payments received within the current business-local calendar month. If
payment allocation is required to show this metric, use payment
records---not invoice creation or update timestamps. - **Missed
promises:** count of open promises whose promised date is before
`asOfDate` and which have not been resolved. Clearly distinguish
system-calculated overdue promises from user-confirmed status.

Avoid double-counting invoices, payments, or customers in grouped
queries. Test totals against hand-calculated examples.

## 8. Import rules and validation

### Initial supported file format

Support CSV first. Add `.xlsx` only if a reliable library is already
available or the user explicitly needs it. Keep parsing behind an import
service so another format can be added without changing financial logic.

Provide a downloadable CSV template with these logical columns: -
CustomerCode (optional) - CustomerName (required) - CustomerEmail
(optional) - InvoiceNumber (required) - InvoiceDate (required) - DueDate
(required) - InvoiceAmount (required, positive decimal) - PaymentAmount
(optional only if its meaning is explicitly defined; prefer a separate
payment import)

For a separate payment import, define columns such as: - CustomerCode or
customer identifier - InvoiceNumber - PaymentDate - PaymentAmount -
PaymentReference (optional)

Do not ambiguously interpret `AmountPaid` as a new payment on every
re-import. A repeated import must not duplicate payments.

### Validation

-   Limit file size and row count using configurable limits.
-   Validate extension and content; do not trust the filename or
    browser-supplied MIME type.
-   Reject unsupported encodings or report them clearly.
-   Trim leading/trailing whitespace in identifiers and names, but do
    not alter invoice numbers in a way that changes identity.
-   Validate dates explicitly; document accepted date formats in the
    template.
-   Validate decimal parsing using an invariant, documented input
    format. Do not depend on the server machine's culture.
-   Reject negative invoice amounts and invalid due dates according to
    an explicitly documented policy.
-   Detect duplicate rows within the same upload and duplicates against
    existing records.
-   Show row number, field, and understandable error message.
-   Never partially import a file without showing which rows were
    committed. Prefer preview, explicit confirmation, and transactional
    persistence.
-   Never overwrite existing invoice/payment data silently. Present a
    duplicate/update policy and make it visible.
-   Escape formula-leading characters when producing CSV error reports
    or exports to reduce spreadsheet formula-injection risk.

### Idempotency

For each import type, define a stable business key and ensure that
repeating the same upload cannot duplicate invoices or payments. Do not
implement payment imports until a safe idempotency key or duplicate
policy is in place.

## 9. Follow-up worklist

The worklist should help the accounts team decide what to do next, not
just display a raw invoice table.

Include: - Customer name - Invoice number - Invoice date and due date -
Outstanding amount - Days overdue (zero or blank for not-overdue
invoices) - Last follow-up date - Next follow-up date, if set -
Promise-to-pay date/status, if any - Reminder status - Quick actions:
view details, add note, record promise, preview reminder, pause
reminders

Default sort order should prioritise: 1. Missed promises and severely
overdue invoices. 2. Older overdue invoices. 3. Larger overdue balances.
4. Due-today/upcoming invoices.

Make sorting deterministic when values tie. Filters and pagination must
be server-side for potentially large lists.

Do not create an opaque "AI priority score" in the MVP. Explain the
sorting rules in the UI or documentation.

## 10. Promise-to-pay behaviour

Users must be able to record: - Promised amount - Promised date -
Related customer and optionally invoice - Notes and who recorded it

Show upcoming promises and missed promises separately.

For the initial version: - A promise is a commitment, not a payment. -
Recording a promise must not reduce invoice outstanding. - A promise
does not automatically mark an invoice paid. - Provide an explicit way
to record the outcome. - If a payment is recorded, do not automatically
mark a promise kept unless the payment clearly matches the promise under
an implemented, tested rule. - Retain a history of changes rather than
silently overwriting important promise outcomes.

## 11. Email reminder behaviour

Implement email as a replaceable service, for example `IEmailSender`,
with: - A development sender that logs or captures messages without
sending externally. - A production sender configured via environment
variables/secrets. - Clear success/failure results and provider message
IDs where available.

### Reminder safety

-   Automated reminders are off by default.
-   Show a preview before the first send and before any bulk send.
-   For bulk sends, show recipient count and a summary of
    amounts/invoices.
-   Do not send if the customer has no valid email, reminders are
    paused, the invoice is disputed, the invoice is paid, or the invoice
    is otherwise ineligible.
-   Do not send repeated reminders for the same invoice and reminder
    stage inside the configured cooldown period.
-   Make schedules, cooldowns, and templates configurable without code
    changes where practical.
-   Use a stable idempotency key or database guard so a retried
    background job does not send duplicate messages.
-   Do not put confidential account information in email subject lines.
-   Provide a plain-text body and a professional, neutral tone.
-   Clearly identify the distributor in the email. Do not impersonate
    the application as the creditor.
-   Avoid threats, shaming language, or claims about legal action.
-   Keep provider errors sanitised and visible to authorised users.
-   Record every attempted send in the reminder log.

Example template:

Subject: Payment reminder for invoice {{InvoiceNumber}}

Hello {{CustomerName}},

Our records show an outstanding balance of {{OutstandingAmount}} for
invoice {{InvoiceNumber}}, due on {{DueDate}}.

If payment has already been made, please disregard this reminder and
share the payment reference so we can update our records.

For any discrepancy, please contact {{BusinessName}} at
{{BusinessContact}}.

Regards,\
{{BusinessName}}

All template variables must be rendered with safe encoding. Do not allow
arbitrary template code execution.

## 12. Security, privacy, and reliability

-   Enforce workspace/tenant isolation on every read, update, delete,
    export, import, and background job.
-   Derive the active workspace from the authenticated user's
    server-side membership. Never trust a submitted tenant ID by itself.
-   Add tests proving that a user from one business cannot read or
    mutate another business's records.
-   Use framework-provided authentication, password hashing, CSRF
    protection, output encoding, and secure cookie defaults.
-   Validate and authorise every server-side action.
-   Keep secrets in user secrets, environment variables, or a secret
    manager. Never put credentials in source control or
    `appsettings.json`.
-   Use HTTPS in deployed environments.
-   Apply file-size limits, safe filenames, and private storage for
    uploaded files.
-   Log security-relevant events without logging passwords, access
    tokens, complete email credentials, or unnecessary customer data.
-   Use generic error messages for unexpected failures; keep technical
    details in protected logs.
-   Add sensible rate limits to login and expensive import/reminder
    endpoints if the hosting setup supports them.
-   Implement backups and document a restore test before using real
    customer data.
-   Do not use real customer data in automated tests or demos. Generate
    synthetic data.
-   Consider applicable Indian privacy and email communication
    requirements before production launch; do not claim legal compliance
    without a proper review.

## 13. UI and usability requirements

The interface should be functional and readable rather than visually
elaborate.

Required pages: 1. Sign in / sign out. 2. Dashboard. 3. Customers list
and customer detail. 4. Invoices list and invoice detail. 5. Import page
with preview and results. 6. Follow-up worklist. 7. Promise-to-pay
form/history. 8. Reminder preview/history. 9. Basic business settings
and reminder settings.

Usability: - Use INR formatting consistently, e.g. `₹1,25,000.00` where
appropriate. - Display dates in a consistent `dd MMM yyyy` format in the
UI. - Make empty states useful, with a clear next action. - Show
validation errors beside the relevant field and summarise import
errors. - Confirm destructive actions. - Preserve search/filter state
when returning from a detail page where practical. - Support keyboard
navigation, visible focus, labels, and accessible colour contrast. - Do
not rely on colour alone to communicate overdue/failed states. - Avoid
excessive animation, complex charts, and unnecessary dashboard widgets.

## 14. Suggested application structure

Adapt to the existing repository rather than forcing this exact layout:

-   `src/Collections.Web/`
    -   MVC controllers, Razor views, authentication, filters, view
        models.
-   `src/Collections.Application/`
    -   Use cases/services, DTOs, validation, interfaces.
-   `src/Collections.Domain/`
    -   Entities, enums, financial rules/value objects where useful.
-   `src/Collections.Infrastructure/`
    -   EF Core DbContext, entity configurations, migrations, email
        provider, import parsing, background jobs.
-   `tests/Collections.UnitTests/`
-   `tests/Collections.IntegrationTests/`

For a very small first slice, a single ASP.NET Core project with clear
folders is acceptable. Do not create projects solely to satisfy this
document if doing so adds needless complexity. Keep domain calculations
independent of MVC and infrastructure either way.

Suggested service boundaries: - `IInvoiceBalanceService` -
`IAgeingService` or a shared financial calculation service -
`IInvoiceImportService` - `IPaymentImportService` (only when payment
import is implemented) - `IFollowUpService` - `IPromiseToPayService` -
`IReminderService` - `IEmailSender` - `ICurrentBusinessContext`

Use names consistent with the codebase. Avoid creating an interface for
every trivial class.

## 15. Test strategy and minimum test cases

### Unit tests --- financial calculations

Test at least: - An unpaid invoice has its full invoice amount
outstanding. - A fully paid invoice has zero outstanding. - A partially
paid invoice has the correct balance. - An invalid allocation cannot
exceed the payment or invoice balance. - Due today is not overdue. - One
day after due date is in the 1--30 day bucket. - Exactly 30 days overdue
is in 1--30. - Exactly 31 days overdue is in 31--60. - Exactly 60 days
overdue is in 31--60. - Exactly 61 days overdue is in 61+. - Paid
invoices are excluded from overdue/ageing totals. - Dashboard totals
match a hand-calculated dataset. - Month boundaries use the
business-local calendar date. - A promise is not a payment and does not
reduce outstanding.

### Import tests

Test: - Valid file and valid rows. - Missing required headers. - Empty
file. - Malformed date and decimal. - Negative/zero invoice amount. -
Duplicate invoice within the file. - Duplicate invoice against existing
records. - Re-import does not create duplicates. - Invalid rows are
reported with correct row numbers. - Malicious filenames and CSV
formula-injection output. - Oversized files and excessive row counts. -
Import failure does not leave an undocumented partial result.

### Security/integration tests

Test: - Unauthenticated users cannot access protected pages. - Business
A cannot read/update/delete Business B's customer, invoice, payment,
follow-up, promise, import, or reminder. - A client-supplied
`BusinessId` cannot bypass tenant scoping. - CSRF protection applies to
state-changing web forms. - Paid, disputed, paused, and email-less
invoices are not sent reminders. - Retrying a reminder job does not send
a duplicate. - Failed email delivery is recorded and visible.

Use deterministic clocks or pass `asOfDate` explicitly into financial
services. Avoid tests that depend on the machine's current date or
culture.

## 16. Operational configuration

Document configuration through environment variables or user secrets,
including: - Database connection string. -
Authentication/data-protection settings appropriate to deployment. -
Email provider endpoint, API key/credentials, sender address, and sender
display name. - Reminder schedule and cooldown values. - Import size and
row-count limits. - Application environment and public base URL.

The app must start in development without production email credentials.
Use a safe no-send email implementation locally.

Provide: - A clear local setup guide in `README.md`. - Database
migration instructions. - Synthetic demo data or a development-only seed
process. - A `.env.example` or equivalent containing placeholders only,
if environment files are used. - A `.gitignore` that excludes secrets,
local databases, generated files, and uploaded customer data.

Never automatically seed demo users/passwords in production.

## 17. Definition of done

A feature is done only when: - Its acceptance criteria are met. -
Server-side validation and authorisation are in place. - Tenant
isolation is maintained. - Relevant automated tests are added and
passing. - Database changes include migrations. - The UI handles
loading, empty, validation, and error states. - No secrets or real
customer data are committed. - The build and relevant tests were
actually run. - Setup/configuration documentation is updated where
needed.

The MVP is pilot-ready when a test business can: 1. Sign in securely. 2.
Import a supported invoice CSV and understand all rejected/duplicate
rows. 3. See correct outstanding and overdue totals. 4. Find invoices by
ageing and customer. 5. Record a follow-up and promise to pay. 6. See
missed promises without confusing them with actual unpaid invoice
balances. 7. Preview and manually send a safe email reminder through a
configured provider, with an audit record. 8. Demonstrate that another
business cannot access its data.

## 18. Recommended implementation sequence

Build and verify one vertical slice at a time:

1.  **Repository baseline:** inspect solution, document local setup,
    confirm build/test commands.
2.  **Foundation:** application configuration, database, migrations,
    authentication, business workspace, tenant scoping.
3.  **Customers and invoices:** CRUD/list/detail with validation and
    indexes.
4.  **Financial calculations:** balance and ageing services plus unit
    tests.
5.  **CSV import:** template download, preview, validation, duplicate
    policy, confirmation, results.
6.  **Dashboard:** totals powered by shared tested calculation logic.
7.  **Follow-up worklist:** filters, sorting, notes, next action.
8.  **Promises to pay:** record, list, resolve, and missed-promise
    display.
9.  **Email reminders:** preview, manual send, provider abstraction,
    audit log, pause/dispute/paid checks.
10. **Pilot hardening:** tenant-isolation tests, failure handling,
    backups, logging, setup guide, synthetic demo data.

Do not begin with all pages and all features at once. Complete each
slice, build, test, and review it before proceeding.

## 19. Product and business guardrails

-   This is a collections workflow aid, not a source of truth for the
    customer's accounting ledger.
-   Make it clear that imported data may be stale until the user
    refreshes it.
-   Show when data was last imported.
-   Provide a way to correct errors without silently changing historical
    records.
-   Do not promise that reminders guarantee faster payment.
-   Avoid paid integrations and recurring infrastructure costs until a
    pilot demonstrates demand.
-   Before expanding the scope, validate with distributors whether the
    feature solves a frequent, expensive problem and whether they would
    pay for it.
-   Keep a small feedback log from pilot users and prioritise recurring
    problems over feature requests from a single user.

## 20. Agent response format for implementation tasks

At the end of each coding task, report:

-   **Implemented:** concise list of behaviour added.
-   **Files changed:** key files and their purpose.
-   **Database/configuration:** migrations or settings required.
-   **Verification:** exact build/test commands run and observed result.
-   **Known gaps:** anything incomplete, untested, or deliberately
    deferred.
-   **Next smallest step:** one logical follow-up task.

Do not claim production readiness, security, compliance, or test success
without evidence.
