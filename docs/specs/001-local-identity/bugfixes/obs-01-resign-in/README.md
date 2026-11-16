# GOZ-0003-OBS-01: signing in again from a signed-in client

**Routing:** maintainer decision GOZ-0003-IF-15, option A (2026-11-16). A bounded bug fix against
GOZ-0001 behaviour, landing before GOZ-0040 starts.

## Defect

When a client that already had a live session signed in again, or registered another account, the
API answered `200`/`201`, but the new session cookie was dead and the next request got `401`.

The cause is in ASP.NET Core's cookie handler: it remembers the session key it authenticated for
the request (`_sessionKey`) and, on sign-in, renews that key instead of storing a new one (see the
10.0.15 `CookieAuthenticationHandler`). Sign-in and registration first deleted the presented session
(session fixation protection), so the renewal found no row (`DbTicketStore.RenewAsync` never
resurrects a deleted session) and the cookie still named the deleted key. Sliding expiration made it
worse: the handler's response-time refresh would have re-issued the old key too.

GOZ-0001's `Every_sign_in_issues_a_new_session_and_retires_the_previous_one` passed only by accident.
Its first re-sign-in got the dead key back, and its second presented that dead key, so the handler
held nothing and stored a new session.

## Fix

`SessionRotation` (`src/Gozleme.Api/Identity/SessionRotation.cs`) ends the presented session through
the request's handler, which deletes the row and stops its refresh. It then issues the new session
through a fresh handler instance that holds no key. The response carries exactly one session cookie.
Sign-in and registration both use it. `DbTicketStore.RemoveCurrentAsync` is removed.

## Evidence

- **Integration tests**, all three failing before the fix and passing after it:
  - `AuthFlowTests.Every_sign_in_from_a_signed_in_client_issues_a_new_live_session_and_retires_the_presented_one` (replaces the incidental test);
  - `AuthFlowTests.Registering_from_a_signed_in_client_issues_a_live_session_for_the_new_account_and_retires_the_presented_one`;
  - `SessionExpiryTests.Signing_in_again_when_the_presented_session_is_due_for_sliding_renewal_still_issues_one_live_new_session`.
- **Browser test** `identity-demo.spec.ts`, "signing in again from a sign-in form opened before the
  browser signed in keeps a working session": it failed on `main` at `0fc67d2` without the fix (the
  user is bounced back to sign-in) and passes with it.
- **Cumulative demo** (`make demo SPEC=GOZ-0003`, GOZ-0000 to GOZ-0003): fresh stack, clean tree at
  `41b18c8`, 34/34 passed. The run record is in [`demo-run.json`](demo-run.json), and the screenshot
  is [`screenshots/09-signed-in-again-session-kept.png`](screenshots/09-signed-in-again-session-kept.png).
  GOZ-0003's own merged evidence was restored afterwards and is unchanged.
