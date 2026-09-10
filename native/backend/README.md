# Same-site synchronization change

Prepared in the maintained checkout, not deployed: nine files recorded by `../evidence/maintained-source.json`. The source baseline is standalone repository `Michaelunkai/game-library-manager-web`, commit `2fc2461955174d90a4542f707b878ef9f0b6cc26`. The checkout itself belongs to the larger `Michaelunkai/repos` repository; pushing that repository is not a verified Netlify deployment route.

The existing project `.gitignore` excludes `package-lock.json`. The prepared lockfile exists and is included in the nine-file receipt; explicitly include it when packaging or staging the reviewed deployment. No commit or push was made here.

Run `node stage-deployment.cjs` from this directory to create an allowlisted local deployment source under `native/evidence/deployment-candidate-<timestamp>/source`. The packager explicitly copies and verifies `package-lock.json`, checks all nine reviewed source hashes, pins and verifies SDK 10.7.13 in both manifest and lock, checks JavaScript syntax, and records every staged file hash in its adjacent manifest. The stage includes the public site, its artwork and data, all four existing Netlify functions, the fetch guard, and deployment configuration. It excludes CLI state, private backups, node_modules, and native artifacts. This operation neither authenticates nor deploys; only use the resulting source for the existing site identified below. The existing optional root `data/admin-config.json` fallback is absent in the checkout; the function's actual `public/data/admin-config.json` fallback is included and verified.

Existing site: **game-library-michaelunkai**, `https://game-library-michaelunkai.netlify.app`, site ID `c8ccb88c-0b80-486f-940b-e89d9acefe99`. Keep its existing functions, environment, integrations, domain, and Blobs store.

## Reviewable behavior

- Admin writes require the revision read by the client. Netlify Blobs receives `onlyIfMatch` against its actual ETag, or `onlyIfNew` when creating the document. Competing writes receive a conflict instead of overwriting each other.
- Both clients preserve a per-field outbox, merge unrelated fields, and leave same-field conflicts for explicit resolution. The browser enables this path only when its backend advertises conditional writes; other hosting paths retain their existing behavior.
- The exact `@netlify/blobs` 10.7.13 package is pinned. A fetch guard prevents failed HTTP writes or empty ETags from being acknowledged as success. Authorization failures remain failures.
- Current Docker tags, metadata, image proxy, and Netlify routing were reconciled with the maintained repository. Catalog data and cover files were not changed.

## Verification and rollback

Run `node verify-cas.cjs`, `node verify-sdk.cjs`, and `node verify-admin-sync.cjs` here. These exercise local fixtures and the actual SDK transport; they do not prove the live provider's behavior. Run `node ../verify-web.cjs` for the isolated original website suite.

Run `node verify-deployment-bundle.mjs` after staging to exercise the installed official Netlify bundler without invoking the Netlify CLI or reading account credentials. It copies the candidate into an isolated fixture, runs its exact `npm ci --ignore-scripts` with empty user/global npm configuration files, and creates all four function ZIPs. The current Node 24 candidate passed 15 checks: each function manifest explicitly targets `nodejs24.x`; both public fallback data files match their reviewed bytes; the admin ZIP contains SDK 10.7.13 and the unchanged fetch guard; the original candidate and lock remain unchanged. Results are in `../evidence/deployment-bundle.json`, with each ZIP hash and its generated Netlify manifest inside the timestamped fixture. The earlier Node 18 candidate and proof remain historical evidence. Local ZIP creation does not establish hosted runtime acceptance or deployed provider behavior.

Run `node rollback-source.cjs` to validate every current source/backup checksum and rehearse all nine restorations inside the private backup directory. It does not change the maintained checkout. `node rollback-source.cjs --apply` restores exactly those nine source paths after preserving the candidate privately; it refuses to overwrite later edits. Private backups must not be committed or served. Run `npm install --ignore-scripts` afterward if dependency files were restored.

## Deployment prerequisite and live acceptance

The saved Netlify session returned HTTP 401 to a read-only site request. Reauthorize the local Netlify CLI through its normal secure login flow for the existing account and **game-library-michaelunkai** site. The account sign-in page is `https://app.netlify.com/login`; signing into that page alone may not refresh CLI authorization. Do not paste tokens into chat. GitHub access is available, but only Vercel and Pages deployment evidence was found; a GitHub push must not be assumed to update this Netlify site.

Before deploying, resolve and record the site's current successful deploy ID as the production rollback point and verify the site ID. Build a draft on this same site, inspect all function routes and static assets, then publish the reviewed deployment through the existing site's authenticated route. No deployment has been attempted here.

After publication, verify actual conditional-write capability, competing same-revision writes (exactly one winner), failure responses, and complete native-to-browser and browser-to-native changes through the approved visible Chrome profile. Record original values before any temporary change and reconcile/restore them before retries. Verify reload, offline queued changes, reconnect, explicit conflict resolution, and preservation of unrelated data. The earlier production API probe has `restored:true`; it did not exercise browser UI.

Production rollback must use the recorded previous successful deployment, leaving the Blobs document and user data intact. Reverting to the legacy writer also removes its atomic concurrency guarantee; inspect queued edits and reconcile their outcomes before further writes. The local source rollback is not a production rollback.
