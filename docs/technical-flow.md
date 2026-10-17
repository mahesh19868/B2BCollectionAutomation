# Technical Flow of the Collections Application

This document follows a request through the application: which controller action handles it, which
services and Core rules it calls, and how the resulting Razor view is rendered in the browser. It
describes the code as it is in the repository. If you change a flow, update this document too.

> Paths are relative to the repository root. Line numbers were correct when this was written; if one
> no longer matches, search for the method name.

---

## 1. Solution layout

| Project | Purpose | Depends on |
|---|---|---|
| `src/Collections.Core` | Entities, enums, and **pure business rules**: balances, ageing, dashboard totals, worklist ordering, promise rules, reminder eligibility and templates, CSV parsing and import planning, INR and date formatting. It has no HTTP or EF Core dependency and is unit-tested directly. | nothing |
| `src/Collections.Web` | ASP.NET Core MVC app: `Program.cs`, controllers, application services, EF Core `AppDbContext` and migrations, Identity, email senders, Razor views, and static assets. | Core |
| `tests/Collections.UnitTests` | xUnit tests for Core rules (financial, imports, reminders). | Core |
| `tests/Collections.IntegrationTests` | `WebApplicationFactory` tests that drive real HTTP requests (security, imports, reminders, workflows, page smoke tests). | Web |

Within `Collections.Web`:

```
Controllers/     Thin MVC controllers. They bind input, call a service, and choose a view or redirect.
Services/        Application services. They load data through AppDbContext and call Core rules.
Data/            AppDbContext (tenant filters, audit stamping), ApplicationUser, SystemNotes helper, Migrations/
Infrastructure/  ICurrentBusinessContext, claims factory, security headers, rate-limit names, Ui view helpers
Email/           IEmailSender plus CaptureEmailSender (development) and SmtpEmailSender (production)
Models/          View models and form models (most form models extend a service *Input class)
Views/           Razor views, one folder per controller, plus Shared/ for the layout and partials
wwwroot/         Bootstrap, site.css, site.js (small progressive-enhancement script)
```

Every request follows the same layering:

```
Browser ──HTTP──▶ Middleware pipeline ──▶ Controller action
                                              │
                                              ▼
                                        Service (Services/*.cs)
                                         │                 │
                                         ▼                 ▼
                              AppDbContext (EF Core)   Core rules (Collections.Core)
                              tenant filter + audit    pure, deterministic functions
                                         │
                                         ▼
                                     SQL Server
                                              │
Controller ◀── view model / ServiceResult ────┘
    │
    ▼
Razor view (+ _Layout, partials, Ui helpers) ──HTML──▶ Browser
```

---

## 2. Application startup — `src/Collections.Web/Program.cs`

`Program.cs` builds the service container and the middleware pipeline. In order:

1. **Database:** `AddDbContext<AppDbContext>` with SQL Server. Startup fails if `ConnectionStrings:DefaultConnection` is empty.
2. **Clock and tenancy:** registers `TimeProvider.System` as a singleton (tests can swap it for a fixed clock) and `ICurrentBusinessContext` → `HttpCurrentBusinessContext` as scoped.
3. **Identity:** `AddIdentity<ApplicationUser, IdentityRole>` with password and lockout rules (10+ characters; 5 failures lock the account for 15 minutes). It plugs in `AppClaimsPrincipalFactory` so the sign-in cookie carries a `business_id` claim.
4. **Cookies:** the auth cookie is `collections.auth`, CSRF is `collections.csrf`, and TempData is `collections.tempdata`. All are `Secure`/`HttpOnly`, and the auth cookie expires after 8 hours with sliding expiration.
5. **Authorization fallback policy:** every endpoint requires an authenticated user **and** a `business_id` claim unless it is marked `[AllowAnonymous]`.
6. **MVC:** `AddControllersWithViews` with a global `AutoValidateAntiforgeryTokenAttribute`, so every POST must carry a valid antiforgery token.
7. **Import limits:** binds `ImportLimits` from the `Import` section and sets the multipart body limit to match.
8. **Email provider:** picks `CaptureEmailSender` (the default in Development) or `SmtpEmailSender` based on `Email:Provider`. Outside Development, startup fails if the provider is not set.
9. **Rate limiting:** a `login` policy (per IP) and an `expensive` policy (per business) for import and reminder endpoints.
10. **Application services:** `BusinessService`, `InvoiceQueryService`, `ActivityQueries`, `DashboardService`, `CustomerService`, `InvoiceService`, `PaymentService`, `FollowUpService`, `WorklistService`, `ImportService`, and `ReminderService`, all scoped (one instance per HTTP request).
11. **Optional migrations:** if `Database:MigrateOnStartup` is `true`, calls `Database.Migrate()`.

### Middleware pipeline (in request order)

| Order | Middleware | Effect |
|---|---|---|
| 1 | `UseExceptionHandler("/Home/Error")` + `UseHsts()` | Non-Development only. Unhandled exceptions render `Views/Shared/Error.cshtml`. |
| 2 | `UseStatusCodePagesWithReExecute("/Home/Status")` | 404/403/429 and similar codes re-execute `HomeController.Status` → `Views/Shared/Status.cshtml`. |
| 3 | `SecurityHeadersMiddleware` (`Infrastructure/SecurityHeadersMiddleware.cs`) | Adds CSP (`script-src 'self'`, no inline scripts), `X-Frame-Options: DENY`, `nosniff`, and Referrer-Policy. |
| 4 | `UseHttpsRedirection` | Redirects HTTP to HTTPS. |
| 5 | `UseRequestLocalization` | Forces the invariant culture so parsing never depends on the server's locale. INR and date formatting are done explicitly. |
| 6 | `UseRouting` | Conventional route `{controller=Home}/{action=Index}/{id?}`. |
| 7 | `UseRateLimiter` | Applies `[EnableRateLimiting]` policies. Rejected requests get 429. |
| 8 | `UseAuthentication` | Reads the `collections.auth` cookie and builds `HttpContext.User`, including the `business_id` claim. |
| 9 | `UseAuthorization` | Enforces the fallback policy. Unauthenticated users are redirected to `/Account/Login?ReturnUrl=…`. |
| 10 | `MapStaticAssets` / `MapControllerRoute` | Serves `wwwroot` anonymously and dispatches to the controller action. |

---

## 3. Cross-cutting mechanics

### 3.1 Tenant (business) isolation

Isolation is enforced in three places, so a controller cannot accidentally leak another business's data.

1. **Where the business id comes from:** `HttpCurrentBusinessContext.BusinessId` (`src/Collections.Web/Infrastructure/CurrentBusinessContext.cs`) reads the `business_id` claim from the encrypted auth cookie and **never** from the request body or query. `AppClaimsPrincipalFactory.GenerateClaimsAsync` (line 63) adds the claim at sign-in from `ApplicationUser.BusinessId`.
2. **Reads:** `AppDbContext.ConfigureTenant` (`src/Collections.Web/Data/AppDbContext.cs:189`) adds a global query filter `e.BusinessId == CurrentBusinessId` to every `ITenantOwned` entity (Customer, Invoice, Payment, PaymentAllocation, ImportBatch, ImportRow, FollowUpActivity, PromiseToPay, ReminderLog). With no signed-in business, `CurrentBusinessId` is `-1`, which matches nothing. As a result, `_db.Invoices.SingleOrDefaultAsync(i => i.Id == id)` returns `null` for another business's id, and the controller returns 404.
3. **Writes:** `AppDbContext.ApplyTenantAndAuditRules` (line 205), called from `SaveChanges`/`SaveChangesAsync`:
   - stamps `BusinessId` on new tenant-owned rows;
   - throws if a row's `BusinessId` differs from the current business, or if `BusinessId` is modified;
   - refuses to modify a `Business` row other than the current one;
   - sets `CreatedAtUtc`/`UpdatedAtUtc` on `IHasTimestamps` entities using the injected `TimeProvider`.

### 3.2 Business-local "today"

`BusinessService.GetTodayAsync` (`src/Collections.Web/Services/BusinessService.cs:49`) converts the current UTC time to the business's time zone with `BusinessCalendar.LocalDate` (`src/Collections.Core/Financial/BusinessCalendar.cs:6`). Every due/overdue, ageing, promise, and month-boundary decision uses this `DateOnly` "as-of date". `BusinessService.GetCurrentAsync` (line 36) loads and caches the current `Business` row for the rest of the request.

### 3.3 Shared balance calculation (single source of truth)

Every page that shows a balance goes through `InvoiceQueryService.GetAsync` (`src/Collections.Web/Services/InvoiceQueryService.cs:38`):

1. It takes an optional `filter` lambda that adds SQL `Where` clauses. This runs on top of the tenant filter.
2. If `nonZeroBalanceOnly` is set, it excludes fully paid invoices in SQL (`InvoiceAmount != Sum(Allocations)`).
3. It projects each invoice with `Allocated = Sum(Allocations.AllocatedAmount)` and the customer fields needed for display and reminders.
4. For every row in memory, it calls **`InvoiceCalculator.Calculate(invoiceAmount, allocated, dueDate, asOfDate)`** (`src/Collections.Core/Financial/InvoiceCalculator.cs:48`), which returns `InvoiceFinancials`:
   - `Outstanding = InvoiceAmount − Allocated`;
   - `InvalidData` if the amount is ≤ 0, the allocations are negative, or the allocations exceed the amount. These are reported, never clamped;
   - `Paid` if outstanding is 0; otherwise `Overdue` / `DueToday` / `NotDue` from `asOfDate − dueDate`;
   - `DaysOverdue`, `IsPartiallyPaid`, and the ageing bucket from `GetBucket` (line 99): ≤0 → NotDue, 1–30, 31–60, 61+.

`InvoiceQueryService.GetOneAsync` (line 82) is the single-invoice version.

### 3.4 Service results and validation messages

Commands such as create, update, and record return `ServiceResult` (`src/Collections.Web/Services/ServiceResult.cs`):

- `Ok(id)`: success, usually followed by a redirect.
- `Fail(errors)`: a list of `ServiceError(Field, Message)`. The controller calls `AppController.AddErrors` (`src/Collections.Web/Controllers/AppController.cs`), which copies each error into `ModelState` under the field name, and re-renders the form. `<span asp-validation-for="…">` shows field errors beside the input, and `<div asp-validation-summary>` shows form-level errors (empty field name).
- `Missing()`: `NotFound = true`. The controller returns `NotFound()`, which the status-code middleware renders as `Views/Shared/Status.cshtml`.

Validation runs **on the server inside the services**. No view currently includes `_ValidationScriptsPartial`, so there is no jQuery client-side validation; errors appear after the form is posted.

### 3.5 Flash messages (Post/Redirect/Get)

After a successful POST, controllers call `Flash("…")` or `FlashError("…")` (stored in TempData) and redirect. The layout renders `Views/Shared/_StatusMessages.cshtml` on the next GET, which shows a green `alert-success` or red `alert-danger` banner once.

### 3.6 Return URLs

Quick actions on the worklist pass `returnUrl=<current filtered URL>`. After the POST, `AppController.RedirectToLocal` redirects back there only if `Url.IsLocalUrl(returnUrl)` is true, which prevents open redirects. This keeps the user's filters and page when they return.

### 3.7 History entries for corrections

`SystemNotes.AddSystemNote` (`src/Collections.Web/Data/SystemNotes.cs`) adds a `FollowUpActivity` with `ActivityType.System` whenever data is corrected or an outcome is recorded: an invoice edit, an import update, a payment recorded or deleted, or a promise resolved. These appear in the History lists on customer and invoice pages, so changes are never silent.

---

## 4. How the UI is rendered

### 4.1 Razor view resolution

1. A controller action returns `View(model)` (or `View("Edit", model)`). MVC finds `Views/<Controller>/<Action>.cshtml`, falling back to `Views/Shared/`.
2. `Views/_ViewStart.cshtml` sets `Layout = "_Layout"` for every view.
3. `Views/_ViewImports.cshtml` imports the `Collections.Web.*` and `Collections.Core.*` namespaces and enables tag helpers (`asp-controller`, `asp-action`, `asp-for`, `asp-validation-for`, `<partial>`, and others).
4. The view is rendered with its strongly typed `@model`. Its HTML is inserted at `@RenderBody()` in the layout.

### 4.2 Layout — `Views/Shared/_Layout.cshtml`

- `<head>` loads `bootstrap.min.css` and `css/site.css`.
- A "Skip to main content" link for keyboard users.
- A **navigation bar**, shown only when `User.Identity.IsAuthenticated`, with links to Dashboard, Worklist, Customers, Invoices, Payments, Promises, Reminders, Import, and Settings. It also shows the user's email and a **Sign out** POST form (which includes the antiforgery token automatically).
- `<main>`: renders `<partial name="_StatusMessages" />` (flash banners), then the page via `@RenderBody()`.
- A footer disclaimer: "Collections workflow aid – not an accounting ledger…".
- Scripts: `bootstrap.bundle.min.js`, `js/site.js`, and any `@section Scripts`.

### 4.3 Shared partials

| Partial | Model | Used by |
|---|---|---|
| `_StatusMessages` | TempData | Layout (every page) |
| `_InvoiceTable` | list of `InvoiceSummary` | Invoices/Index, Customers/Details (with `noCustomerColumn`) |
| `_PromiseTable` | list of `PromiseItem` | Promises/Index, Customers/Details, Invoices/Details |
| `_ReminderTable` | list of `ReminderItem` | Reminders/Index, Customers/Details, Invoices/Details |
| `_ActivityList` | list of `ActivityItem` | Customers/Details, Invoices/Details (History) |
| `_Pager` | `(page, totalPages, totalCount)` | All paginated lists. It uses `Ui.PageUrl` so the current filters are kept. |

### 4.4 View helpers — `src/Collections.Web/Infrastructure/Ui.cs`

Views never calculate money or status themselves. They only format values the services have already computed:

- `Ui.Money(decimal)` → `MoneyFormat.Inr` (`src/Collections.Core/Formatting/MoneyFormat.cs:25`), for example `₹1,25,000.00` with Indian digit grouping.
- `Ui.Date(DateOnly)` → `dd MMM yyyy`. `Ui.DateTime(utc, timeZoneId)` converts audit timestamps to the business time zone.
- `Ui.Badge(...)` + `Ui.Tag(...)` render a Bootstrap badge that **always includes text** (for example "Overdue" or "Date passed – unresolved"), so meaning never relies on colour. The text is HTML-encoded by `TagBuilder`.
- `Ui.Label(...)` gives readable names for ageing buckets, import row actions and outcomes, and activity types.
- `Ui.Lines(text)` splits stored multi-line messages (import errors and notes) for display.

All user content (customer names, notes, and so on) is written with `@value`, which Razor HTML-encodes. No view uses `Html.Raw` on user data.

### 4.5 Client-side JavaScript — `wwwroot/js/site.js`

The script is small, and every rule it supports is also enforced on the server:

1. **`data-confirm`:** a click on any element with `data-confirm="…"` shows `window.confirm`, and the action is cancelled if the user declines. It is used on the import **Confirm** button (`Import/Batch.cshtml`), the reminder **Send** button (`Reminders/Preview.cshtml`), and the reminder **Retry** button (`Reminders/Details.cshtml`). Deleting a payment uses a separate confirmation page instead (`Payments/Delete.cshtml`).
2. **Double-submit guard:** on submit of any POST form, it marks the form as submitted and disables its submit buttons.
3. **Payment allocation helper:** active only on a form with `data-allocation-form` (Payments/Create). It shows running "Allocated" and "Unallocated" totals as the user types, and an **Auto-allocate** button fills invoices in order up to the payment amount. It works in paise (integers) to avoid floating-point errors.

The CSP forbids inline scripts, so all behaviour is in this file.

---

## 5. Feature flows

Each subsection lists the route, the controller action, the service and Core calls, and the view rendered.

### 5.1 Register a business and sign in

**Register:** `GET /Account/Register`
- `AccountController.Register()` (`src/Collections.Web/Controllers/AccountController.cs:63`) is `[AllowAnonymous]`. It redirects to the dashboard if the user is already signed in; otherwise it renders `Views/Account/Register.cshtml` with an empty `RegisterViewModel`.

**Submit registration:** `POST /Account/Register` (rate-limited by the `login` policy)
- `AccountController.Register(model)` (line 68):
  1. If `ModelState` is invalid (data annotations on `RegisterViewModel` in `Models/AccountModels.cs`), it re-renders the form.
  2. Opens a DB transaction and adds a `Business` (name, contact email). `SaveChangesAsync` runs; a `Business` is not tenant-owned, so it is allowed.
  3. `UserManager.CreateAsync(new ApplicationUser { BusinessId = business.Id }, password)`. On failure it rolls back, adds the Identity errors to `ModelState`, and re-renders.
  4. Commits, then `SignInManager.SignInAsync`, which calls `AppClaimsPrincipalFactory` to add the `business_id` claim to the cookie.
  5. Flashes "Your workspace is ready…" and redirects to `Home/Index`.

**Sign in:** `GET /Account/Login` → `Views/Account/Login.cshtml`. `POST /Account/Login` (line 34) calls `SignInManager.PasswordSignInAsync(..., lockoutOnFailure: true)`. On success it calls `RedirectToLocal(returnUrl)`; otherwise it shows a generic "Invalid email or password" or the lockout message.

**Sign out:** the layout's Sign out button posts to `/Account/Logout` (line 103) → `SignOutAsync` → redirect to Login.

**Unauthenticated access:** the fallback policy fails, and the cookie handler redirects to `/Account/Login?ReturnUrl=…`.

### 5.2 Dashboard

`GET /` or `/Home/Index` → `HomeController.Index` (`src/Collections.Web/Controllers/HomeController.cs:18`) → `DashboardService.GetAsync` (`src/Collections.Web/Services/DashboardService.cs:32`):

1. `BusinessService.GetCurrentAsync` and `GetTodayAsync` get the business and its as-of date.
2. `InvoiceQueryService.GetAsync(null, nonZeroBalanceOnly: true, today)` loads all unpaid invoices with their `InvoiceFinancials`.
3. Loads this month's `Payments` (business-local month of `today`) as `PaymentAmount` records.
4. Loads open `PromisesToPay` as `PromiseSnapshot` records.
5. **`DashboardCalculator.Compute(financials, payments, promises, today)`** (`src/Collections.Core/Financial/DashboardCalculator.cs:36`) produces `DashboardTotals`: total outstanding, overdue, due today, due in the next 7 days (inclusive, excluding overdue), collections this month, missed-promise count, ageing bucket totals, and the count of invalid invoices.
6. Loads up to 10 missed promises (open, `PromisedDate < today`), the latest committed import time, and the customer and invoice counts.

View: `Views/Home/Index.cshtml` (`@model DashboardData`):
- The header shows "As of dd MMM yyyy (business-local date)" and "Data last imported: …".
- **Empty state:** if `InvoiceCount == 0`, it shows a card with **Import invoices**, **Add a customer**, and **Review settings** buttons.
- A warning banner if any invoices have a data error, with a link to `Invoices?Status=DataError`.
- Four KPI cards: Total outstanding, Overdue (links to `Worklist?Status=Overdue`), Due in next 7 days, and Collections this month. Below them are the ageing buckets and the missed-promises list.

### 5.3 Follow-up worklist

`GET /Worklist?Status=&Bucket=&Search=&MinAmount=&Promise=&DueForFollowUpOnly=&Page=` → `WorklistController.Index(WorklistFilter)` (`src/Collections.Web/Controllers/WorklistController.cs:16`). Model binding fills `WorklistFilter` from the query string. The controller then calls `WorklistService.GetAsync` (`src/Collections.Web/Services/WorklistService.cs:67`):

1. Gets today, then loads outstanding invoices through `InvoiceQueryService.GetAsync`, with an optional SQL search on invoice number, customer name, or code. It keeps only `HasOutstandingBalance` rows.
2. For the involved customers and invoices, loads non-system `FollowUpActivities`, open `PromisesToPay`, and `ReminderLogs`.
3. For each invoice, builds a `WorklistItem`:
   - **Last follow-up:** the latest user activity for the customer.
   - **Next follow-up date:** from the latest activity for this invoice or for the whole account.
   - **Promise:** the earliest open promise for this invoice or account. `PromiseRules.IsOverdue` sets `HasMissedPromise`, and `PromiseRules.GetDisplayState` (`src/Collections.Core/Financial/PromiseRules.cs:24`, `:27`) gives its badge.
   - **Last reminder status:** from the latest reminder log for the invoice.
   - **Priority group** from `WorklistPrioritizer.PriorityGroup` (`src/Collections.Core/Financial/WorklistPrioritizer.cs:26`): 1 = missed promise or 61+ days overdue, 2 = other overdue, 3 = due today or upcoming.
4. Applies the in-memory filters: status, bucket, minimum amount, promise filter, and "due for follow-up only".
5. Sorts with **`WorklistPrioritizer.Sort`** (line 36): by group; within groups 1–2 by most days overdue, then larger balance; within group 3 by earliest due date, then larger balance; ties broken by invoice number, then id. The order is deterministic.
6. Paginates (50 per page) and returns `WorklistResult` with the filtered outstanding total.

The controller also passes `CurrentUrl` (path + query) so quick actions can return the user to this filtered page.

View: `Views/Worklist/Index.cshtml`:
- A GET filter form (status, ageing bucket, search, minimum amount, promise filter, due-for-follow-up checkbox) and a **Reset** link.
- The text of `WorklistPrioritizer.Explanation`, which tells the user how the list is ordered.
- **Empty state:** "Nothing to follow up for these filters", with links to **Clear filters** and **Import invoices**.
- A table with one row per invoice: customer, invoice number (plus a **Disputed** badge), dates, outstanding, days overdue, last and next follow-up, the promise badge, the reminder status badge, and a **Paused** badge.
- Quick actions per row:
  - **Note** → `GET /FollowUps/Create?customerId&invoiceId&returnUrl`
  - **Promise** → `GET /Promises/Create?customerId&invoiceId&returnUrl`
  - **Reminder** → `GET /Reminders/Preview?invoiceId`
  - **Pause/Resume reminders** → `POST /Invoices/Flags/{id}` with `remindersPaused` and `returnUrl`
- `_Pager` at the bottom.

### 5.4 Customers

| Route | Action (`CustomersController.cs`) | Service call (`CustomerService.cs`) | View |
|---|---|---|---|
| `GET /Customers?search=&page=` | `Index` (17) | `ListAsync` (73): SQL search and paging on customers, then `InvoiceQueryService.GetAsync` for those customer ids to compute outstanding, overdue, and open-invoice counts per customer | `Customers/Index.cshtml` + `_Pager` |
| `GET /Customers/Details/{id}` | `Details` (23) | `GetDetailAsync` (108): the customer, all invoices with financials, and (through `ActivityQueries`) payments, promises, activities, and reminders | `Customers/Details.cshtml` with `_InvoiceTable`, `_ActivityList`, `_PromiseTable`, `_ReminderTable` |
| `GET /Customers/Create` | `Create` (30) | — | `Customers/Edit.cshtml` (shared create/edit form) |
| `POST /Customers/Create` | `Create` (33) | `CreateAsync` (137): validates and enforces code uniqueness | Redirect to Details, or re-render the form with errors |
| `GET/POST /Customers/Edit/{id}` | `Edit` (47 / 64) | `GetDetailAsync` / `UpdateAsync` (152) | `Customers/Edit.cshtml` |
| `POST /Customers/PauseReminders/{id}` | `PauseReminders` (84) | `SetRemindersPausedAsync` (171) | Redirect to `returnUrl` or Details |

`ActivityQueries` (`src/Collections.Web/Services/ActivityQueries.cs`) holds the shared read-only projections for payments, activities, promises, and reminders. It also resolves user ids to emails, limited to users of the same business.

### 5.5 Invoices

| Route | Action (`InvoicesController.cs`) | Service call (`InvoiceService.cs`) | View |
|---|---|---|---|
| `GET /Invoices?Status=&Bucket=&Search=&CustomerId=&Page=` | `Index` (19) | `ListAsync` (76): `InvoiceQueryService.GetAsync`, then a status/bucket filter, ordering by due date then number, and paging | `Invoices/Index.cshtml` + `_InvoiceTable` + `_Pager` |
| `GET /Invoices/Details/{id}` | `Details` (25) | `GetDetailAsync` (123): the summary with financials, external reference, payment allocations, promises, activities, and reminders | `Invoices/Details.cshtml` |
| `GET /Invoices/Create?customerId=` | `Create` (32) | `CustomerService.ListActiveForSelectAsync` fills the customer dropdown | `Invoices/Edit.cshtml` |
| `POST /Invoices/Create` | `Create` (40) | `CreateAsync` (164) | Redirect to Details, or re-render the form |
| `GET /Invoices/Edit/{id}` | `Edit` (55) | `GetForEditAsync` + `GetDetailAsync` | `Invoices/Edit.cshtml` (the invoice number and customer are read-only) |
| `POST /Invoices/Edit/{id}` | `Edit` (73) | `UpdateAsync` (209): validates, refuses an amount below what is already allocated, builds a change list, and adds a system note such as "Invoice X edited: amount ₹… → ₹…" | Redirect to Details |
| `POST /Invoices/Flags/{id}` | `Flags` (97) | `SetFlagsAsync` (274): toggles disputed and/or reminders-paused | Redirect to `returnUrl` or Details |

`Invoices/Details.cshtml` shows the status badges (Overdue, Partially paid, Disputed, Reminders paused), a data-error alert if one applies, action buttons (**Preview reminder**, **Add note**, **Record promise**, **Record payment**, **Correct invoice**, **Dispute/Pause** toggles), the invoice facts, the payment allocations table, and the promises, reminders, and history partials.

### 5.6 Payments and allocations

**Two-step create form:** `GET /Payments/Create[?customerId=]` → `PaymentsController.Create` (`src/Collections.Web/Controllers/PaymentsController.cs:35`) → `ShowFormAsync`:
- **Step 1 (no customer):** fills `CustomerOptions` from `CustomerService.ListActiveForSelectAsync`, and the view shows a customer picker.
- **Step 2 (customer chosen):** loads `PaymentService.GetOpenInvoicesAsync(customerId)` and builds one `AllocationInput` row per open invoice, keeping any amounts already typed. The payment date defaults to `BusinessService.GetTodayAsync`.
- View: `Payments/Create.cshtml`, a form with `data-allocation-form`. `site.js` provides the live totals and the Auto-allocate button.

**Record a payment:** `POST /Payments/Create` → `PaymentService.RecordAsync` (`src/Collections.Web/Services/PaymentService.cs:104`):
1. Validates the date (required, not in the future), the amount (`MoneyRules.ValidatePositiveAmount`), reference and notes length, that the customer exists in this tenant, and that the reference is not a duplicate for this customer.
2. Opens a **serializable** transaction, so two concurrent payments cannot both allocate the same outstanding balance.
3. Reloads the target invoices through `InvoiceQueryService.GetAsync` (restricted to this customer) and builds `AllocationRequest`s.
4. **`AllocationRules.Validate(paymentAmount, allocations)`** (`src/Collections.Core/Financial/AllocationRules.cs:15`): each allocation must be positive and no more than the invoice's outstanding balance, and the total must not exceed the payment amount. Any remainder stays as a visible unallocated amount.
5. Saves the `Payment` with its `PaymentAllocation`s and adds a system note ("Payment of ₹… recorded; allocated to …"). Commits.
6. The controller flashes "Payment recorded." and redirects to `Payments/Details/{id}`.

**Delete:** `GET /Payments/Delete/{id}` shows a confirmation page (`Payments/Delete.cshtml`). `POST` → `PaymentService.DeleteAsync` (212) removes the payment and its allocations, so invoice balances are restored. It adds a system note and redirects to the customer.

The list (`GET /Payments`) and details (`GET /Payments/Details/{id}`) use `ListAsync` and `GetDetailAsync`.

### 5.7 Follow-up notes

`GET /FollowUps/Create?customerId=&invoiceId=&returnUrl=` → `FollowUpsController.Create` (`src/Collections.Web/Controllers/FollowUpsController.cs:19`) → `FollowUpService.GetTargetAsync` checks that the customer or invoice exists in the tenant (404 otherwise) and supplies the names shown on the form. View: `FollowUps/Create.cshtml`.

`POST /FollowUps/Create` → `FollowUpService.AddActivityAsync` (`src/Collections.Web/Services/FollowUpService.cs:78`): validates the type (Call/Email/Note/Other), that notes are present, the outcome length, and that the next follow-up date is not in the past. It saves a `FollowUpActivity` and redirects to `returnUrl`, or else to the invoice or customer details. The worklist's "next follow-up" and "last follow-up" columns come from these records.

### 5.8 Promises to pay

Promises are handled by `PromisesController`, which is in the same file as `FollowUpsController`.

| Route | Action | Service call (`FollowUpService.cs`) | View |
|---|---|---|---|
| `GET /Promises?view=Open\|Missed\|Upcoming\|Resolved\|All` | `Index` (69) | `ListPromisesAsync` (262): Missed = open with date before today; Upcoming = open with date today or later | `Promises/Index.cshtml` + `_PromiseTable` |
| `GET /Promises/Create?customerId&invoiceId&returnUrl` | `Create` (78) | `GetTargetAsync` | `Promises/Create.cshtml` |
| `POST /Promises/Create` | `Create` (94) | `AddPromiseAsync` (137) | Redirect, with the flash "…It does not change the invoice balance." |
| `GET /Promises/Resolve/{id}` | `Resolve` (118) | `GetPromiseAsync` (209) | `Promises/Resolve.cshtml` |
| `POST /Promises/Resolve` | `Resolve` (125) | `ResolvePromiseAsync` (216) | Redirect to `returnUrl` or the customer |

Rules applied:
- `AddPromiseAsync` requires a positive amount and a date that is not in the past. It uses `InvoiceQueryService` to make sure the promise does not exceed what is owed (the invoice balance, or the customer's total for an account-level promise). A promise **never** creates an allocation, so outstanding balances do not change.
- `ResolvePromiseAsync` checks `PromiseRules.CanResolveTo` (only Open → Kept/Missed/Cancelled). It then runs a **conditional update** (`WHERE Status = Open`), so two users cannot record different outcomes, and adds a system note. A resolved promise cannot be changed again.
- **"Date passed – unresolved"** is a system-calculated display state from `PromiseRules.GetDisplayState`. It is shown separately from the user-confirmed `Missed` status.

### 5.9 CSV import

```
Import/Index ──upload──▶ Import/Upload ──▶ Import/Batch/{id} (preview)
                                              │            │
                                     Confirm ─┘            └─ Cancel
                                              ▼
                                 Import/Batch/{id} (results) ──▶ Import/Errors/{id} (CSV)
```

**Import page:** `GET /Import` → `ImportController.Index` (`src/Collections.Web/Controllers/ImportController.cs:27`) → `BuildIndexAsync` collects `ImportService.RecentAsync(20)`, `Limits`, and the time zone. View: `Import/Index.cshtml`, with the upload form, a template download link, the accepted formats, and recent imports.

**Template:** `GET /Import/Template` returns `InvoiceCsvReader.BuildTemplate()` (`src/Collections.Core/Imports/InvoiceCsvReader.cs:46`) as a UTF-8 CSV with a BOM.

**Upload:** `POST /Import/Upload` (rate-limited, request-size-limited) → `ImportService.CreatePreviewAsync` (`src/Collections.Web/Services/ImportService.cs:38`):
1. `FileNameSanitizer.Sanitize` strips any path and unsafe characters from the client file name.
2. Reads the stream into memory, stopping as soon as the size passes `MaxFileBytes`.
3. **`InvoiceCsvReader.Read(bytes, fileName, limits)`** (`InvoiceCsvReader.cs:55`) does the file-level checks: `.csv` extension, not empty, size, strict UTF-8 decoding, `CsvParser.Parse` (RFC 4180), header row present, no duplicate headers, required columns present, and the row-count limit. It adds notices for unknown or payment-like columns and for blank rows. If any check fails, the controller re-renders `Import/Index` with `FileErrors` and **nothing is stored**.
4. `PlanAsync` loads existing customers, and the existing invoices that match the file's invoice numbers, and calls **`InvoiceImportPlanner.Plan(rows, customers, invoices)`** (`src/Collections.Core/Imports/InvoiceImportPlanner.cs:64`):
   - field validation (required fields, dates using `ImportValueParser.TryParseDate`, amounts using `TryParseAmount`, email, and lengths);
   - an invoice number repeated in the file rejects every occurrence;
   - conflicting customer codes are rejected;
   - customers are matched by code, then by name; otherwise a new customer is planned;
   - invoices are matched by number: identical → `Unchanged`, different → `Update` (applied only if approved), belongs to another customer → error, not found → `Create`.
5. Saves an `ImportBatch` (status `Previewed`) with one `ImportRow` per data row, holding the raw values, planned action, errors, and notes. The uploaded file itself is **not** kept.
6. Redirects to `Import/Batch/{id}`.

**Preview and results page:** `GET /Import/Batch/{id}` → `ImportService.GetBatchAsync` → `Views/Import/Batch.cshtml` with `ImportBatchModel`:
- **When previewed:** "Summary – nothing has been imported yet", counts by planned action, and an expiry warning after 24 hours. It offers a **Confirm** form with an "approve updates" checkbox (shown only if there are updates) and **Discard this preview**.
- **When committed, failed, or cancelled:** a result summary with created, updated, skipped, and rejected counts.
- A row table with the row number, values, the planned action or outcome label (`Ui.Label`), red ✖ errors, and notes.
- **Download rejected rows (CSV)**, if any rows were rejected.

**Confirm:** `POST /Import/Confirm/{id}?approveUpdates=` → `ImportService.CommitAsync` (line 114):
1. **Claims** the batch atomically (`Previewed` → `Committing`, and only within 24 hours), so a double submit cannot run twice.
2. Opens a **serializable** transaction and **re-plans** the stored rows against the current data, because data may have changed since the preview.
3. For each row: `Create` adds the invoice, plus a new customer shared by rows with the same key; `Update` (if approved) changes the invoice's dates and amount and adds a system note; otherwise the row is recorded as skipped or rejected. Every row gets an `Outcome`.
4. Marks the batch `Committed`, saves, and commits. On any exception it clears the change tracker, marks the batch `Failed` with a user-friendly message, and returns an error. **Nothing is partially saved.**
5. Imports never send reminders and never change existing customers' details.

**Cancel:** `POST /Import/Cancel/{id}` → `CancelAsync` (102) sets `Previewed` → `Cancelled`.

**Error report:** `GET /Import/Errors/{id}` → `BuildErrorReportAsync` → `ImportErrorReport.Build` (`src/Collections.Core/Imports/ImportErrorReport.cs:9`), which uses `CsvWriter.Escape` (`src/Collections.Core/Imports/CsvParser.cs:122`) to prefix a `'` to values that start with `=`, `+`, `-`, `@`, tab, or carriage return (spreadsheet formula injection).

### 5.10 Email reminders

```
Invoice/Worklist ──▶ Reminders/Preview?invoiceId ──POST Send──▶ Reminders/Details/{logId}
                                                                     │
                                                             POST Retry (if Failed)
```

**Preview:** `GET /Reminders/Preview?invoiceId=` → `RemindersController.Preview` (`src/Collections.Web/Controllers/RemindersController.cs:41`) → `ReminderService.PreviewAsync` (`src/Collections.Web/Services/ReminderService.cs:54`):
1. Loads the business, today, and the invoice through `InvoiceQueryService.GetOneAsync`.
2. `ReminderEligibility.BuildTemplateValues` (`src/Collections.Core/Reminders/ReminderEligibility.cs:85`) builds the placeholder values (customer, invoice number and dates, amount, outstanding formatted as INR, business name and contact).
3. `ReminderTemplateRenderer.RenderSubject` and `Render` (`src/Collections.Core/Reminders/ReminderTemplates.cs:65`, `:69`) replace `{{Placeholder}}` tokens with plain text substitution only, with no code execution. Control characters are removed from the subject to prevent header injection.
4. `GetBlockingReasonsAsync` (line 245) loads recent `Sent`/`Queued` logs for the invoice and calls **`ReminderEligibility.Evaluate`** (`ReminderEligibility.cs:25`). That returns every reason the reminder is blocked: no or invalid email, customer inactive, customer or invoice paused, disputed, paid, data error, a send already in progress, or within the cooldown. The service also adds a reason if the business contact email is missing.
5. Creates a new `IdempotencyKey` (a GUID) for the form.

View: `Reminders/Preview.cshtml` shows the recipient, from/reply-to, subject, and plain-text body. If the reminder is blocked, it shows a warning listing **all** blocking reasons and no Send button. If it is eligible, it shows a **Send** POST form with a hidden `idempotencyKey` and `data-confirm`. There is also an **Edit template** link to Settings. Nothing is sent from this page.

**Send:** `POST /Reminders/Send` (rate-limited) → `ReminderService.SendAsync` (line 79):
1. If a log with the same idempotency key already exists, it returns that log instead of sending again (this handles double clicks and refreshes).
2. Calls `PreviewAsync` again, so **eligibility is re-checked at send time**. If the reminder is now blocked, it flashes the reasons and redirects back to Preview.
3. Inserts a `ReminderLog` with status `Queued` (content, recipient, outstanding amount, key). The unique index `(BusinessId, IdempotencyKey)` resolves concurrent duplicate submissions.
4. `DeliverAsync` (line 212) calls `IEmailSender.SendAsync(EmailMessage)`:
   - `CaptureEmailSender` (`src/Collections.Web/Email/EmailSenders.cs`) writes a `.txt` file to `App_Data/outbox` and never contacts a server;
   - `SmtpEmailSender` uses MailKit. The From display name is the distributor, and Reply-To is the business contact email. Errors are mapped to sanitised messages.
5. Updates the log to `Sent` (with `SentAtUtc` and the provider message id) or `Failed` (with a sanitised `LastError`).
6. The controller flashes a success or failure message and redirects to `Reminders/Details/{logId}`.

**Retry:** `POST /Reminders/Retry/{id}` → `RetryAsync` (line 144). Only `Failed` logs can be retried. It re-checks eligibility and refuses if the balance or recipient has changed since the reminder was written. It then claims the log atomically (`Failed` → `Queued`, incrementing `AttemptCount`) and calls `DeliverAsync` again.

**History:** `GET /Reminders?status=&page=` → `HistoryAsync` (193) → `Reminders/Index.cshtml` + `_ReminderTable` + `_Pager`. `GET /Reminders/Details/{id}` → `GetLogAsync` → `Reminders/Details.cshtml`, which shows the full audit record and a Retry button for failed reminders.

Reminders are **never** sent automatically. There is no scheduler in the current code.

### 5.11 Settings

`GET /Settings` → `SettingsController.Index` (`src/Collections.Web/Controllers/SettingsController.cs:18`) fills `SettingsFormModel` from `BusinessService.GetCurrentAsync`. View: `Settings/Index.cshtml`, with business name and contact, cooldown days, and the subject and body templates with the list of allowed placeholders.

`POST /Settings` → `BusinessService.UpdateSettingsAsync` (`BusinessService.cs:55`) validates the name, email, phone, a cooldown of 1–90 days, and both templates using `ReminderTemplateRenderer.Validate` (unknown placeholders and length). It then saves and clears the cached business.

`POST /Settings/ResetTemplate` loads `ReminderTemplateDefaults` into the form **without saving**. The user must review the template and click Save.

### 5.12 Errors and status pages

- **Unhandled exception** (non-Development): `HomeController.Error` → `Views/Shared/Error.cshtml` shows a generic message and a request id. Details stay in the logs.
- **404/403/429 and similar codes:** `HomeController.Status(code)` → `Views/Shared/Status.cshtml` with a friendly message for the code. Another business's record id produces a 404 here, because of the tenant query filter.

---

## 6. End-to-end example: recording a promise from the worklist

1. The user opens `/Worklist?Status=Overdue`. Then `WorklistController.Index` → `WorklistService.GetAsync` → `InvoiceQueryService.GetAsync` → `InvoiceCalculator.Calculate` for each invoice → `WorklistPrioritizer.Sort`. Finally `Worklist/Index.cshtml` renders the table.
2. The user clicks **Promise** on a row. The browser requests `GET /Promises/Create?customerId=7&invoiceId=42&returnUrl=/Worklist?Status=Overdue`.
3. `PromisesController.Create` → `FollowUpService.GetTargetAsync(7, 42)`. Both lookups go through the tenant filter, so another business's ids would return 404. Then `Promises/Create.cshtml` renders a form with a hidden antiforgery token.
4. The user submits. `POST /Promises/Create` passes the antiforgery check and binds `PromiseFormModel`.
5. `FollowUpService.AddPromiseAsync` validates the input and checks the amount is no more than the invoice's outstanding balance (from `InvoiceQueryService`). It adds the `PromiseToPay` and calls `SaveChangesAsync`, where `ApplyTenantAndAuditRules` stamps the `BusinessId`.
6. The controller flashes "Promise to pay recorded. It does not change the invoice balance." Then `RedirectToLocal(returnUrl)` sends the user back to `/Worklist?Status=Overdue`.
7. The worklist reloads. The row now shows an **Upcoming** promise badge. When the promised date passes without the promise being resolved, the badge becomes **Date passed – unresolved** and the row moves to priority group 1.

---

## 7. Configuration that affects the flows

From `src/Collections.Web/appsettings.json`. Override these with environment variables (for example `Email__Provider=Smtp`) or user secrets. Never commit real credentials.

| Key | Effect |
|---|---|
| `ConnectionStrings:DefaultConnection` | SQL Server database. Required. |
| `Database:MigrateOnStartup` | Apply EF migrations at startup (default `false`). |
| `Import:MaxFileBytes`, `Import:MaxRows` | Upload size and row limits (2 MB / 5000 by default). |
| `Email:Provider` | `Capture` (writes to a folder) or `Smtp`. Development defaults to `Capture`. |
| `Email:FromAddress`, `Email:Smtp:*` | SMTP envelope sender and relay settings. |
| `Email:CaptureDirectory` | Output folder for captured emails (`App_Data/outbox`). |
| `RateLimits:LoginPerMinute`, `RateLimits:ExpensivePerMinute` | Rate-limit policies. |
| `DataProtection:KeysDirectory` | Persist cookie-encryption keys (needed for multi-instance or restart-safe deployments). |

Per-business settings (time zone, reminder cooldown, templates, contact details) are stored on the `Business` row and edited on the Settings page.

---

## 8. Where to look when changing something

| If you change… | Start in | Also check |
|---|---|---|
| Balance, status, or ageing rules | `Core/Financial/InvoiceCalculator.cs` | `tests/Collections.UnitTests/Financial/*` |
| Dashboard totals | `Core/Financial/DashboardCalculator.cs`, `Web/Services/DashboardService.cs` | `DashboardCalculatorTests` |
| Worklist ordering | `Core/Financial/WorklistPrioritizer.cs` | `Views/Worklist/Index.cshtml` (explanation text) |
| Import validation or duplicates | `Core/Imports/InvoiceImportPlanner.cs`, `InvoiceCsvReader.cs` | `Web/Services/ImportService.cs`, import unit and integration tests |
| Reminder eligibility | `Core/Reminders/ReminderEligibility.cs` | `Web/Services/ReminderService.cs`, reminder tests |
| Tenant isolation | `Web/Data/AppDbContext.cs`, `Infrastructure/CurrentBusinessContext.cs` | `tests/Collections.IntegrationTests/SecurityTests.cs` |
| Navigation or page chrome | `Views/Shared/_Layout.cshtml` | `wwwroot/css/site.css` |
| Money or date display | `Core/Formatting/MoneyFormat.cs`, `Web/Infrastructure/Ui.cs` | — |
| Schema | Entity in `Core/Entities`, mapping in `AppDbContext.OnModelCreating` | Add an EF migration under `Web/Data/Migrations` |
