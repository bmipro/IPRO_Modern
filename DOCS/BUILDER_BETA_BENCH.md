# The new builder (Beta) on your own PC

A test bench for TODO 585: iPro's portal, SuperAdmin and the new builder running together on this PC,
so you can try the whole path yourself before anything goes anywhere. Nothing here is on the internet,
and your own builders (5087 and 5088) are not touched.

## Start and stop

Start (in PowerShell, from the iPro checkout that has the Beta branch):

```
powershell -ExecutionPolicy Bypass -File ops\Start-BuilderBench.ps1
```

It builds the three apps (a minute or two the first time), starts each in its own minimized window,
and prints the three addresses:

- the portal: http://localhost:5110/portal
- SuperAdmin: http://localhost:5210
- the builder: http://localhost:5310 (it opens from My Website; on its own it says so)

Stop:

```
powershell -ExecutionPolicy Bypass -File ops\Stop-BuilderBench.ps1
```

Drafts made in the builder stay for next time (its database `ipro_builder_bench` and the folder
`C:\Users\admin\Projects\bb-bench`).

## Try it

1. **SuperAdmin** (http://localhost:5210): sign in with the local admin name and password of your local
   settings. Open **Agents**, pick a test agent (QADailyTest has a website), open **Template Builder**.
   In the panel **The new builder (Beta) for this customer**, tick **On** and press **Save**. Leave
   **They publish what they send themselves** off to try the staff path first.
2. Still in SuperAdmin, on the agent's page, use **Reset Password** to get a temporary password for that
   test agent (the local test accounts only).
3. **Portal** (http://localhost:5110/portal): sign in as that agent with the temporary password (it asks
   for a new one). Open **My Website**: the card **New website builder (Beta)** is there. Press **Try the
   new builder (Beta)**.
4. **In the builder:** the top says whose website it is. Change a heading on the page, add a section,
   press **Undo** and **Redo**, **Save**. Press **See it on my site**: a new tab shows the page in the
   site's real design. Press **Send to iPro**.
5. **Back in SuperAdmin**, the agent's Template Builder list shows **Sent by the customer from the new
   builder**. Review it, look at the pages, **Bring it in**. (The "it is live" email goes nowhere from
   this PC unless your local settings send mail.)
6. **On My Website** the card now offers **Put my previous site back**.
7. To try the other path, tick **They publish what they send themselves** in SuperAdmin, open the
   builder again, send, and the builder takes you to your own review page with **Publish**.

Things to try on purpose: open the builder, then change the same page in the page editor (the page list
warns you, and publishing replaces that change); add a section in the page editor while a draft is
open (it is kept); switch the Beta off while the builder is open (within five minutes the builder says
the session ended); close the builder and press **Continue my draft** the next day.
