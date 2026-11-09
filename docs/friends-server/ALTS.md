# Alts: account warehouse and player mail

Two things that make playing several characters easier, both switched by `Aion:Alts` (code: `server/Aion.World/AltRules.cs`,
`GameSession.Alts.cs`; storage: `server/Aion.Infrastructure/Postgres/PostgresAltsStore.cs`; migration
`041_alts_account_warehouse_player_mail.sql`). Both are on by default. With both off the server behaves as before them.

| Key | Default | Meaning |
| --- | --- | --- |
| `Aion:Alts:SharedWarehouse` | `true` | The Storage NPC's warehouse is shared by all of the account's characters on the world. |
| `Aion:Alts:PlayerMail` | `true` | Players can send mail, Kinah and items with chat commands. |
| `Aion:Alts:MailFee` | `0` | Kinah the sender pays for each mail (OURS). |
| `Aion:Alts:MailDays` | `365` | Days a player's mail waits before it expires (staff mail keeps 30). |

As environment variables: `Aion__Alts__SharedWarehouse=false`, `Aion__Alts__MailFee=100`, and so on. The startup log
prints the values in effect (`Alts: ...`).

## Account warehouse

**What the client tells us.** The client's `EItemContainerType` has both `kStorage` (2) and `kCharacterStorage` (15), and
its UI strings come in pairs (`Storage_Move` / `CharStorage_Move`, `StorageItem_Add` / `CharStorageItem_Add`). So the
client knows two warehouses, and `kStorage` (what the Storage NPC and `ItemMove_RQ` already use here) is most likely the
account warehouse (CANDIDATE, from the names; never seen on the official server). There is no separate packet for it:
the warehouse is container 2 in the item list (0x5611 / 0x561b) and in `ItemMove_RQ` (0x563a), as before.

**What the server does.** With `SharedWarehouse` on, container 2 is the account's warehouse on that world:

- Items in it are stored with location `account_storage` and the owning account, world and race. Both races share
  world 1204, so an account's Elyos and Asmodian characters (`Aion:Friends:BothFactions`) keep separate warehouses. Any of
  the account's characters of that race sees them and can take them out.
- A character's old per-character warehouse joins the account warehouse the first time that character loads its items
  (its slots are kept where they are free). Nothing is lost.
- Soul-bound items cannot go in (the same rule as trading). A soul-bound item that was already in a character's own
  warehouse joins the account warehouse with the rest, but only that character can take it out.
- Container 15 (`kCharacterStorage`) is not served: the client was never seen showing it.

**Two characters of one account online at once.** The server does not stop it (two launches of the same account both
reach the world). Duplication is guarded in the database:

- Every warehouse move runs in one transaction, holds an advisory lock for the account's warehouse, and checks the
  item's revision. A second session acting on an old view (an item the other character already took) is refused.
- After each move, the account's other characters get `pg_notify('aion_inventory', ...)`, so their sessions reload
  their items and the warehouse view updates without relogging (needs the game database; `InventoryNotifications`).

**Turning it off later.** Items already in the account warehouse are not shown while the switch is off. They are not
deleted and come back when it is turned on again.

## Player mail

**What the client tells us.** The client (5.3.2.0) has a "write mail" popup and its error messages
(`AionDialogWidget_Post_WritePopup`, `MSG_POST_SEND_COMPLETE`, `MSG_POST_CANNOT_SEND_UNKNOWN_PC` ...), but its packet
catalogue has no send request: the Post messages are only `LoadPost`, `RecvPost_NT`, `ReadPost`, `DeletePost`,
`TakePost` and `RetrievePostNoticeInfo_NT` (0xe214-0xe21c here). The popup cannot reach the server, so sending is done
with chat commands. Receiving uses the client's real mailbox.

**Commands** (typed in chat by any player while `PlayerMail` is on):

| Command | What it does |
| --- | --- |
| `//mail <name> <subject> \| <text>` | A letter. The `\|` separates the subject from the text. |
| `//sendkinah <name> <amount> [note]` | Sends Kinah (commas allowed: `1,000,000`). |
| `//senditem <name> <bag slot> [count]` | Sends an item from the bag, all of the stack unless a count is given. |
| `//senditem` | Lists your bag as `slot: item id x count` so you can find the slot number. |

- `<name>` is any character on the server, on your account or another one, online or not. Your own name is refused.
- Bag slots count from 1 in the order the bag lays items out (`//senditem` shows them). A number of 1000 or more is read
  as an item id instead (the first stack of that item in the bag).
- The mail arrives in the recipient's mailbox (Reward tab when something is attached, System tab otherwise). An online
  recipient gets it at once. The title starts with the sender's name, e.g. `[Elyra] Gear up`.
- The Kinah or item leaves your bag when you send. The item itself travels (its enchant and other upgrades are kept) and
  waits until the recipient takes the attachment. Soul-bound items cannot be mailed. A mail with an attachment not yet
  taken cannot be deleted.
- Mail only reaches characters of the sender's race: it never crosses to the other faction.
- Staff keep the builder command `//Mail <character> <item id|0> <count> <text>` for system mail (its `Title|Body`
  has a `|` too), so for staff `//mail` is always the builder's. Staff send player mail with `//pmail`, which takes the
  same arguments as a player's `//mail`.
- A mail can't cost more than the long range: Kinah plus `MailFee` past it is refused.

Limits (OURS): subject 40 characters (longer is cut), text 500 characters.

## Not done / guesses

- `kStorage` = account warehouse is CANDIDATE (from the client's names).
- How the mailbox shows the title and text comes from the staff-mail template (Post.dat 106, `"{title}|{body}"`), CANDIDATE.
- Mail expiry: an unclaimed item or Kinah waits as long as the mail (`MailDays`). After that it goes back to the
  sender as a new post titled `Returned: ...`, which is never returned again. Any mailbox load sweeps the whole server for
  expired mail.
- The `TakePost` increase reason in `GameSession.Mail.cs` is 29, while the client's `EIncreaseReason` lists
  `kPostAttachment` = 21. Not changed here.
