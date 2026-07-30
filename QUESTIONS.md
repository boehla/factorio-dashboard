# Open questions — answered during planning

> **Historical document.** These questions and answers come from the RCON version. Two points
> have been settled by the mod rewrite: question 1 (achievements) no longer applies the way it
> did, because the mod does not use `/silent-command` — though its mere presence as a
> non-vanilla mod disables achievements anyway. And the worry about the full resource scan is
> moot now that the ore scan runs rolling, chunk by chunk. The rest still holds.

I built plan phases 0–6 as a working skeleton (C# backend compiles, tests green; React
frontend built). In a few places I had to make assumptions, or I need a decision before this
goes live against the real server. Answers go directly under each question.

## A. Blocking fundamentals (from plan §10)

1. **Achievements** — `/silent-command` permanently disables achievements for the save. Is
   that settled and fine with your fellow players?
   → Answer: yes, fine

2. **Reachability / auth** — Does the collector run on the same machine as the server (then
   `--rcon-bind 127.0.0.1`, no auth needed), or does the dashboard have to be reachable over
   LAN or a reverse proxy? For the latter I would put basic auth in front of the hub and the
   research endpoint.
   → Answer: yes, same machine

3. **Auto-research writing for real** — Should the automation actually set research on its
   own, or only preview it ("would set X")? Currently: off by default, toggle in the frontend,
   preview while off.
   → Answer: when auto-research is enabled in the frontend, set the research

## B. Connection and server details (needed for real testing)

4. **RCON access** — Host, port, password of your server (or is port 27015 / local fine)?
   Lives in `src/Fdash.Api/appsettings.json` under `Collector`.
   → Answer: yes, that should work

5. **Shared file access** — Can the collector read the server's `script-output/` directory
   directly (same machine or volume)? If so, please provide the path → faster prototype
   export. If not, I use the RCON fallback.
   → Answer: yes, that should work

6. **Save setup** — One save only, or do you switch between several? (Multi-save via seed
   fingerprint is built in; only relevant if you run two saves from the same seed — then
   `save_name` has to go into the hash.)
   → Answer: we might switch between saves

## C. Calibration (only measurable against your real save — plan §10.2)

7. **Tick cost** — Roughly how large is your base (number of assemblers / map size)? The
   assembler full scan every 5 s and the resource chunk size (starting at 200 chunks per poll)
   have to be calibrated against the real tick time. The adaptive throttle lowers the chunk
   size automatically, but a starting value from you helps.
   → Answer: no chunk querying — if it gets too heavy, rebuild it as a dedicated mod that runs
   inside the game

8. **Poll intervals** — Are the default intervals fine (power/assemblers/trains/logistics 5 s,
   production/platforms 10 s, drills 30 s, full resource cycle ~100 s)? Or gentler?
   → Answer: fine

## D. Feature details where I made an assumption

9. **Alert thresholds** (§10.4) — I made them configurable with defaults: power warning below
   95 %, fuel low below 25 %, stall warning after 60 s. Do those fit, or other values?
   → Answer: yes, fine

10. **Orbital requests** (§3.7) — The Space Age platform logistics API changed between 2.0
    releases. I wrote the snippet defensively (`get_logistic_point` → sections/filters), but it
    needs verifying against your specific Factorio version. Which one runs (e.g. 2.0.x)?
    → Answer: it should run on the current one (2.1.12 experimental)

11. **Platform fuel capacity** — I currently assume 24000 as the thruster fluid maximum
    (placeholder). Do you know the real value, or should I sum it from the thruster prototypes
    plus connected tanks (more work)?
    → Answer: fine, keep it simple

12. **Icons** (§10.6) — Extract modded item icons from the mod zips (effort), or leave them out
    for now and show text names only? Currently: text only.
    → Answer: please extract them

13. **Tagged circuits** (§3.9) — The convention is the prefix `FDASH:` in the combinator
    description. Does that work, or would you prefer the backer name or a different prefix?
    → Answer: works

## E. Technical follow-ups for further work

14. **Database location** — The SQLite file currently sits relative to the API process
    (`fdash.db`). Should it go to a fixed path (e.g. next to the save, or in a data directory)?
    → Answer: fine as is

15. **Deployment target** — Linux or Windows for the collector? I can add a ready-made
    `dotnet publish` profile (`linux-x64` or `win-x64`, self-contained).
    → Answer: Windows for now

16. **Priorities for the next step** — What should I harden against the real server first, once
    you have the credentials? (Suggestion: phase 0 connection test → power/assemblers → then
    the risky resource scan, plan §9.)
    → Answer: yes, let's do that once these questions are implemented
