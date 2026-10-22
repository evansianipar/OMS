
# OMS

## Deploying the ASP.NET Core app to Vercel

Vercel's Container Runtime can run the OMS image built from `Dockerfile.vercel`. The Container Runtime is currently a beta feature. Confirm that it is enabled and approved for the Vercel team before using this for production.

### Deploy

1. Push this repository to a Git provider supported by Vercel.
2. In Vercel, import the repository as a project. Keep the project root at the repository root so Vercel finds `Dockerfile.vercel`.
3. Add the production environment variables in **Project Settings → Environment Variables**:
   - `Authentication__Google__ClientId`
   - `Authentication__Google__ClientSecret`
   - `GoogleWorkspace__Domain`
   - `GoogleWorkspace__SpreadsheetId`
   - `GoogleWorkspace__SheetsUser`
   - `GoogleWorkspace__AppsScriptId`
   - `GoogleWorkspace__CredentialDirectory` (for a temporary test deployment, set to `/tmp/oms-credentials`)
4. Deploy a preview first and inspect the container logs. After verifying the app, promote/deploy to production and assign the approved domain.
5. In Google Cloud OAuth settings, register these callback URLs using the final HTTPS domain:
   - `https://<domain>/signin-google`
   - `https://<domain>/signin-google-sheets`
6. Verify login, the Sheets connection, participant import, attendance updates, and individual/bulk email invitations.

Do not commit OAuth secrets or the Google Sheets refresh token. Rotate any OAuth client secret that has been exposed.

### Production prerequisite: persistent credentials and data-protection keys

The current OMS stores the Google Sheets refresh token in a file under the local application data directory by default. `GoogleWorkspace__CredentialDirectory` can override this path; on Vercel, `/tmp/oms-credentials` avoids colliding with the published `/app/OMS` executable. `/tmp` is only a temporary workaround and is not durable shared storage: redeployments, instance replacement, or multiple instances can lose the Sheets connection. ASP.NET Core Data Protection keys also use local storage by default and can prevent authentication cookies/OAuth state from being decrypted across instances.

Before relying on this deployment for production, replace the local credential/key storage with durable storage shared by all instances (for example, an approved managed database or secret/key-management service). Keep the refresh token encrypted at rest, and configure a stable Data Protection key ring and application name. Do not assume that a file written by `/account/connect-sheets` will survive a redeployment.

On Vercel, the app processes one `X-Forwarded-Proto` hop before HTTPS redirection and OAuth challenge handling. The container should only receive requests through Vercel's proxy; do not expose the container directly to arbitrary clients.
