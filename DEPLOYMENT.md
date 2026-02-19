# Deploying JMScheduler on Windows Server

## 1. Get the app onto the server

### Option A: Clone and build on the server

1. Install **.NET 8 SDK** (or Runtime if you only need to run): https://dotnet.microsoft.com/download/dotnet/8.0  
   - SDK: needed if you build on the server.  
   - Runtime only: use if you copy pre-built output from another machine.

2. Clone the repo (or copy the project folder):
   ```cmd
   git clone https://github.com/ranjansoni/Schedular.git C:\Apps\Schedular
   cd C:\Apps\Schedular\JMScheduler\src\JMScheduler.Job
   ```

3. Build release output to a deploy folder:
   ```cmd
   dotnet publish -c Release -o C:\Apps\JMScheduler
   ```
   This puts the exe, DLLs, and `appsettings.json` (from publish) into `C:\Apps\JMScheduler`.  
   **Note:** Published output does not include `appsettings.json` if it was gitignored; you add it in step 2.

### Option B: Copy from your dev machine

1. On your dev machine, from the project folder:
   ```cmd
   dotnet publish -c Release -o C:\Temp\JMScheduler
   ```
2. Copy the entire `C:\Temp\JMScheduler` folder to the server (e.g. `C:\Apps\JMScheduler`).
3. On the server, install **.NET 8 Runtime** if not already installed: https://dotnet.microsoft.com/download/dotnet/8.0 (choose “Run desktop apps” or “Runtime”).

---

## 2. Configure the app

1. In the deploy folder (e.g. `C:\Apps\JMScheduler`), create or edit **appsettings.json**.
2. Copy from **appsettings.example.json** (in the repo) if the folder doesn’t have it:
   - Get the example from the repo or from `C:\Apps\Schedular\JMScheduler\src\JMScheduler.Job\appsettings.example.json` after cloning.
3. Set the production connection string and any overrides:
   ```json
   {
     "ConnectionStrings": {
       "SchedulerDb": "Server=YOUR_RDS_HOST;Port=3306;Database=janitorialmgr;User Id=YOUR_USER;Password=YOUR_PASSWORD;SslMode=Required;ConnectionTimeout=30;DefaultCommandTimeout=300;"
     },
     "Scheduler": {
       "AdvanceDays": 90,
       "MonthlyMonthsAhead": 12,
       "AuditRetentionDays": 3
     }
   }
   ```
4. Ensure the **logs** folder exists or can be created (e.g. `C:\Apps\JMScheduler\logs`). The app writes to `logs/scheduler-YYYYMMDD.log`.

---

## 3. Test run

1. Open Command Prompt or PowerShell **as the user that will run the scheduled task**.
2. Go to the deploy folder:
   ```cmd
   cd C:\Apps\JMScheduler
   ```
3. Run:
   ```cmd
   JMScheduler.Job.exe
   ```
   Or with .NET:
   ```cmd
   dotnet JMScheduler.Job.dll
   ```
4. Check console output and `logs\scheduler-*.log` for errors. Confirm in the DB that `job_scheduler_run` and `job_shift_audit_log` get new rows.

---

## 4. Schedule with Task Scheduler

1. Open **Task Scheduler** (taskschd.msc).
2. **Create Basic Task** (or Create Task for more options).
3. **Name:** e.g. `JMScheduler`.
4. **Trigger:** Daily (or as needed), e.g. 2:00 AM. Set time zone if important.
5. **Action:** Start a program.
   - **Program/script:** `C:\Apps\JMScheduler\JMScheduler.Job.exe`  
     (or `dotnet.exe` if you run the DLL).
   - **Add arguments (optional):** leave blank for “run now” (current time). For testing you can pass a date: `"2026-02-10 02:00:00"`.
   - **Start in:** `C:\Apps\JMScheduler`.
6. **General tab:**
   - Choose “Run whether user is logged on or not” if the job must run when no one is logged in.
   - “Run with highest privileges” is usually not needed unless the app or DB require it.
   - Use an account that has network access to RDS and write access to `C:\Apps\JMScheduler` and its `logs` folder.
7. **Settings tab:**
   - Consider “Allow task to be run on demand” for manual runs.
   - “If the task fails, restart every” is optional (e.g. restart once after 5 minutes).
8. Save. Run the task once **manually** and check logs and DB again.

---

## 5. Checklist

- [ ] .NET 8 Runtime (or SDK) installed on the server  
- [ ] App in a dedicated folder (e.g. `C:\Apps\JMScheduler`)  
- [ ] `appsettings.json` with **production** connection string (no dev DB/passwords)  
- [ ] `logs` folder present or creatable by the task account  
- [ ] One-off test run from that folder succeeds and writes to DB  
- [ ] Task Scheduler task runs at the desired time and uses the same “Start in” folder  
- [ ] Firewall/security: server can reach Aurora (port 3306 or your port)  
- [ ] DB user has required privileges (SELECT/INSERT/UPDATE/DELETE/CALL on the used tables and SPs)

---

## 6. Updating the app

1. On the server (or on dev then copy):
   - Pull latest from GitHub (if using Option A) and run `dotnet publish -c Release -o C:\Apps\JMScheduler` again, **or**
   - Copy new build output over `C:\Apps\JMScheduler`, **except** `appsettings.json` (keep your existing production config).
2. Run the task once on demand to verify.
3. No need to change the scheduled task unless the exe path or “Start in” folder changes.

---

---

# Deploying JMScheduler API on IIS

## 1. Build and Publish the API

From the repo root:
```cmd
cd JMScheduler
dotnet publish src\JMScheduler.Api\JMScheduler.Api.csproj -c Release -o C:\Apps\JMSchedulerApi
```

## 2. Configure

1. Create or edit `appsettings.json` in `C:\Apps\JMSchedulerApi`:
   ```json
   {
     "ConnectionStrings": {
       "SchedulerDb": "Server=YOUR_RDS_HOST;Port=3306;Database=janitorialmgr;User Id=YOUR_USER;Password=YOUR_PASSWORD;SslMode=Required;"
     },
     "ApiKey": "YOUR_STRONG_RANDOM_API_KEY",
     "Scheduler": {
       "AdvanceDays": 45,
       "MonthlyMonthsAhead": 3
     }
   }
   ```
2. Ensure `logs\` folder exists or can be created by the IIS app pool identity.

## 3. Set Up IIS Site

1. Install the **.NET 8 Hosting Bundle** on the server: https://dotnet.microsoft.com/download/dotnet/8.0 (choose "Hosting Bundle").
2. Open IIS Manager, create a new site (or application under an existing site):
   - **Physical path:** `C:\Apps\JMSchedulerApi`
   - **Binding:** choose your port (e.g. 5050 or 80/443 with a hostname).
3. Set the **Application Pool** to "No Managed Code" (the .NET Core module handles it).
4. The included `web.config` uses InProcess hosting. If you need OutOfProcess, edit `hostingModel`.
5. Browse to `http://yourserver:5050/api/scheduler/status` — you should see `{"status":"healthy",...}`.

## 4. API Endpoints

### Health Check (no auth required)
```
GET /api/scheduler/status
```

### Run Scheduler (requires X-Api-Key header)
```
POST /api/scheduler/run
Content-Type: application/json
X-Api-Key: YOUR_API_KEY

{
  "companyId": 0,
  "modelId": 0,
  "advanceDays": 45,
  "monthlyMonthsAhead": 3
}
```

All fields are optional. Omit or pass 0 to use defaults.

**Examples:**
- Run all companies, 45 days: `{"advanceDays": 45}`
- Run specific company, 90 days: `{"companyId": 123, "advanceDays": 90}`
- Run specific model, 365 days: `{"modelId": 456, "advanceDays": 365}`

### Response (200 OK)
```json
{
  "runId": "guid",
  "status": "Completed",
  "shiftsCreated": 27000,
  "duplicatesSkipped": 5000,
  "overlapsBlocked": 12,
  "orphanedDeleted": 0,
  "resetDeleted": 0,
  "weeklyModelsLoaded": 3200,
  "auditEntries": 32000,
  "conflicts": 12,
  "durationSeconds": 45,
  "errorMessage": null
}
```

### Error Responses
- **401 Unauthorized:** Missing or invalid `X-Api-Key` header.
- **409 Conflict:** Another scheduler run is already in progress.
- **500 Internal Server Error:** Unexpected failure (check logs).

## 5. Calling from Crontab

Using crontab.org or any HTTP scheduling service:
```bash
curl -X POST https://yourserver/api/scheduler/run \
  -H "Content-Type: application/json" \
  -H "X-Api-Key: YOUR_API_KEY" \
  -d '{"advanceDays": 45}'
```

Schedule this to run daily (e.g. 2:00 AM EST).

---

## 7. Troubleshooting

- **“Missing appsettings.json”:** Ensure `appsettings.json` exists in the same folder as `JMScheduler.Job.exe` (or the DLL when using `dotnet`). Copy from `appsettings.example.json` and fill in values.
- **Connection timeouts:** Check firewall, security groups, and that the DB host/user/password are correct in `appsettings.json`.
- **Task runs but no DB changes:** Run the exe manually from the same folder and user; check `logs\scheduler-*.log` and `logs.jobtracking` / `job_scheduler_run` in the DB.
- **StartEvent returns 0:** Another instance is already running or a previous run didn’t call CompleteEvent; check `logs.jobtracking` and DB for stuck sessions; fix or wait for timeout, then rerun.
