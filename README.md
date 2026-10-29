# Copy Trader for NinjaTrader 8 (Rithmic)

A NinjaTrader 8 add-on that copies trades from one **leader** account to any number of **follower** accounts.
It uses only NinjaTrader's Account API, so it works with Rithmic and any other NT8 connection
(CQG/Tradovate, Sim101 and so on). Leader and followers can be on different connections, for example
several prop-firm Rithmic logins connected at the same time.

## Install

1. Close any NinjaScript Editor windows.
2. Copy the `AddOns/CopyTrader` folder into
   `Documents\NinjaTrader 8\bin\Custom\AddOns\`
   so you end up with `...\bin\Custom\AddOns\CopyTrader\CopyTraderEngine.cs` and the other files.
3. In NinjaTrader open **New > NinjaScript Editor** and press **F5** to compile.
4. Open **Control Center > New > Copy Trader**.

## Use

1. Connect your Rithmic connection(s) (**Connections** menu) so the accounts show up.
2. Pick the **Leader account**.
3. **Add follower** for each account to copy into, and set:
   | Column | Meaning |
   |---|---|
   | On | Include this follower when you press Start |
   | Ratio | Follower size = leader size x ratio, truncated toward zero (0.5 of a 1-lot = 0, so it is not copied) |
   | Reverse | Trade the opposite direction of the leader |
   | Max qty | Hard cap on the follower's position per instrument |
   | Instrument map | Trade a different contract, e.g. `NQ=MNQ; ES=MES` (same expiry month) |
4. Press **Start**. Trade the leader however you like: chart trader, DOM, ATM strategies or automated strategies.

Buttons:
- **Sync now**: re-checks every copied instrument right away and un-halts followers that were halted after rejects.
- **Stop + flatten followers**: kill switch. Stops copying, flattens every position and cancels working orders on all enabled followers.
- **Resync every N s**: periodic self-correction (default 5 s, 0 = off).

Settings are saved to `Documents\NinjaTrader 8\CopyTrader.xml`. The log appears in the window and in the
NinjaScript Output window.

## How it copies

The copier mirrors **positions**, not individual orders. Whenever the leader's position in an instrument
changes, each follower is sent a **market order** that takes it to `leader position x ratio` (capped at Max qty).

Why this design:
- **Self-correcting.** A missed fill, reject, partial fill or reconnect gets fixed on the next leader change or periodic resync.
- **Safe reversals.** A follower never crosses through zero in one order: it closes first, then opens the new side.
- **No double fires.** Only one copy order per follower and instrument is in flight at a time. The next one waits for the fill and position update to arrive.
- **Rejects are bounded.** After 3 rejects in a row (for example a daily loss limit), the follower is halted and logged until you press **Sync now**.

## Using it with Apex Trader Funding

Leave **Apex rules** ticked (it's on by default). It blocks **Reverse**, because Apex counts opposite
positions across your accounts as hedging, which is prohibited. It also warns you if you copy across more
than 20 accounts.

Suggested setup:
- **Leader:** one Apex account (an evaluation or PA account) that **you trade by hand**. Most readings of Apex's
  rules allow copying from orders you place yourself into your own accounts. Copying from an automated strategy
  on the leader is the disputed area, so avoid it unless Apex confirms it's allowed.
- **Followers:** only your own Apex accounts. Never copy to or from someone else's account.
- **Max qty:** set this to each account's contract limit. For PA accounts still under the half-size scaling
  limit, set it to the scaled-down number.
- **Ratio:** keep it at 1 when all accounts are the same size. Use the instrument map (e.g. `NQ=MNQ`)
  for a smaller account rather than a fractional ratio.
- **Blown accounts:** if Apex liquidates or disables an account, its orders get rejected. After 3 rejects the
  follower is halted and the others keep copying. Untick it before you start next time.
- **End of day:** close the leader before Apex's daily cutoff. Followers close with it.

Apex changes its rules often. Check the current rules and Prohibited Activities pages in your Apex dashboard
before running this on funded accounts.

## Things to know before you trade it live

- **Test on Sim first.** Use Sim101 as the leader and one or two sim accounts as followers.
- **Followers only get market orders.** Leader stop and target orders (ATM brackets) are *not* placed on followers. When the leader's stop or target fills, the followers exit at market a moment later. Followers have **no resting stop of their own**, so if NinjaTrader or your internet goes down, they stay unprotected. Keep the **Stop + flatten followers** button handy, and know how to flatten directly in R | Trader if you need to.
- **Expect some slippage.** Follower fills land after the leader's fill (typically tens to a few hundred ms, depending on Rithmic routing and your connection).
- **Followers in copied instruments belong to the copier.** If you trade a copied instrument manually on a follower, the copier puts it back in line with the leader. Instruments the leader hasn't traded are left alone.
- **Starting with an open position.** If the leader already holds a position when you press Start, you'll be asked to confirm. Followers are then brought into line with it immediately.
- **Closing the window** asks whether to stop copying or keep running in the background. Recompiling NinjaScript stops the copier.
- This is not financial advice. Use at your own risk.

## Files

| File | Purpose |
|---|---|
| `CopyTraderAddOn.cs` | Adds the Control Center menu item |
| `CopyTraderWindow.cs` | Settings / control window |
| `CopyTraderEngine.cs` | Copy logic (position reconciliation, order tracking, safety) |
| `CopyTraderSettings.cs` | Settings model and XML persistence |
