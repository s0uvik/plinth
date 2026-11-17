# Distribution, sign-in and Claude Desktop access (proposal)

Status: proposal for review, 2026-11-17. Nothing here is built yet.

## Goal

A BIM user signs up on a web portal, downloads Plinth, installs the Revit add-in and works with Plinth by talking to
Claude Desktop through MCP. Access is per user, can be withdrawn, and does not get in the way.

## The original idea

1. A web page where users sign up and then get a download.
2. Each user has a key that they can change or reset.
3. After installing the add-in, the user logs the add-in in with their email and key.
4. The same key goes into the MCP configuration of Claude Desktop.

### What works

Sign-up, a download page, a credential the user can reset, and the add-in checking who is using it are all right.

### What to change

| Problem | Why it matters |
|---|---|
| The key sits in plain text in `claude_desktop_config.json` | That file gets copied, shared and pasted into screenshots and support chats. A leaked key works on any PC until someone notices. |
| The MCP does not need its own key | The MCP server runs on the user's PC and only talks to the local Plinth service, which talks to Revit. If the add-in has signed in, the MCP can rely on that. A second copy of the secret doubles the places it can leak. |
| Typing an email and a long key into Revit | Friction, and people end up keeping keys in a notepad file. |
| A reset breaks two places at once | After a reset the user must update both the add-in and the Claude Desktop config. |

## Proposed design

### 1. Web portal

- Sign-up with email verification; two-factor sign-in available.
- Download page for the installer.
- **My devices**: every PC that is signed in, when it was last seen, and **Sign out this PC**.
- **Team accounts**: BIM offices buy seats, so an office admin can add and remove users and see their devices.

### 2. One signed installer

- `Plinth-Setup.exe` installs the add-in for each Revit year found on the PC (2024–2027), the local Plinth service,
  and the Claude Desktop extension (see 4).
- The installer and the add-in DLL are **code-signed** (Authenticode). Revit warns about unsigned add-ins, and
  offices often block unsigned software.

### 3. Sign in through the browser, not by typing a key

1. The Plinth tab in Revit shows **Sign in**.
2. It opens the portal in the browser; the user signs in and clicks **Approve this PC**.
3. The PC receives its own token, stored in **Windows Credential Manager** (encrypted for that Windows user), never in
   a text file.

This is the standard device sign-in pattern (OAuth 2.0 device authorization, or browser sign-in with PKCE), as used
by Microsoft and GitHub tools. Each PC has its own token, so signing one PC out (from **My devices**) does not affect
the others. That replaces "reset key".

### 4. Claude Desktop with nothing to paste

- The MCP server ships as a **Claude Desktop extension**: one click to install, and it shows in Claude Desktop's
  settings.
- It talks only to the local Plinth service on the same PC, using a **local token** created at install and readable
  only by that Windows user. The user never sees or types a key.
- The MCP server stays local (stdio): it has to reach Revit on the user's PC, which a server on the internet cannot.

### 5. Personal access key, as a fallback only

For locked-down offices where browser sign-in is not possible:

- shown **once** when created, stored only as a hash on the server;
- has an expiry date and a name ("site office laptop");
- revocable from the portal at any time.

This keeps the original idea for the cases that need it.

### 6. Licence checks that work offline

- After sign-in the server gives the PC a **signed licence** (for example valid 7 days) that the add-in verifies
  without internet, and renews quietly when online.
- A site office with poor internet keeps working; a revoked PC stops at the latest when its licence expires.

## Security work needed whichever login design is chosen

- **Lock down the local service.** Today it listens on `127.0.0.1:8765` without a login, so any program on the PC,
  or a malicious website using browser tricks against localhost, could send it commands. Before customers use it:
  require the local token on every request, check the request's `Host`/`Origin`, and protect the review page against
  cross-site requests.
- **Developer mode** (running code inside Revit) stays off and cannot be switched on through the add-in in customer
  builds.
- **Say plainly what leaves the PC.** The Revit model and the drawings stay local; only sign-in, licence and update
  checks go to the Plinth server. With Claude Desktop, the conversation goes to Anthropic under the user's own Claude
  account. Offices will ask.
- **Signed updates.** A signed update check, so fixes can be pushed and a bad version withdrawn.
- **No Anthropic API key on users' PCs.** Today the brief agent calls Claude with `ANTHROPIC_API_KEY`. With Claude
  Desktop the user's own Claude does the reasoning, so the service should not need a key. If a feature still does,
  the Plinth server holds it, never the user's PC.

## The user's experience, end to end

1. Sign up on the portal, verify the email, download `Plinth-Setup.exe`.
2. Run it: the add-in, the service and the Claude Desktop extension are installed.
3. Open Revit → Plinth tab → **Sign in** → approve in the browser.
4. Open Claude Desktop: the Plinth tools are already there.
5. Laptop lost or replaced: portal → **My devices** → **Sign out this PC**.

## Recommendation

Build browser sign-in plus the Claude Desktop extension, and keep a personal access key only as the fallback. It is
about the same effort as the original idea, more secure, and easier for users.

## Open decisions

- Portal hosting and identity provider (build sign-in ourselves, or use a hosted service such as Auth0, Clerk or
  Microsoft Entra ID).
- Licence length offline (7 days proposed) and devices allowed per user.
- Code-signing certificate (organisation validation or EV).
- Pricing and seats for team accounts.
