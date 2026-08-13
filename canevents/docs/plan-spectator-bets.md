# Ставки зрителей на исход матча (CAN_Events)

## Контекст

Мод уже умеет дуэли со ставками участников, зрителей и выплаты предметами. Фича «ставки зрителей»
превращает каждый матч в событие для всего сервера: любой игрок (не только привилегированный
observer) ставит предметы на одну из сторон идущего/собирающегося матча; после финиша пул
проигравших ставок делится между угадавшими пропорционально размеру их ставки. Инфраструктура
(списание/выдача предметов, персист, зрители, сеть, карточки матчей в GUI) уже есть — фича в
основном «склейка».

В план также входит первичная сборка ранее написанных, но не скомпилированных фич этой сессии
(HeadToHeadStore, мутаторы) — они в тех же файлах.

## Ключевые решения

- **Не переиспользуем `EntryFeeEscrow`** — он однослотовый (один ModData-блоб на игрока) и занят
  входными взносами. Ставки живут в новом сторе, предметы списываются сразу.
- **Ставить может любой онлайн-игрок**, кроме участников матча (и ботов-матчей целиком).
  Панель зрителя (SpectatorPanelGui) — нефокусируемый HUD, кнопки там не работают
  (Messages.Spectate.cs:55-63) → размещение ставки идёт из **EventBrowserGui**, карточка
  running-матча (EventBrowserGui.Events.cs:57, клик уже есть — :90-111).
- **Окно ставок**: от появления матча в списке до `EventState.Running` (Lifecycle.cs:233).
  Серверная проверка при размещении; хука на Running нет — и не нужен.
- **Ключ ставки — `RunningEvent.Id`**: персистентен через рестарты (RunningEvent.cs:40
  SeedIdCounter), уникален.
- **Расчёт — в подписчике `OnMatchFinished`** (CANEventManager.cs:161): там доступны и
  RunningEvent, и MatchResult; сразу после матч удаляется из live-списка.
  `OnMatchAborted` (:164) → полный возврат.
- **Исходы**: победила сторона, на которую ставил → своя ставка назад + доля пула проигравших
  (пропорционально числу предметов в ставке, floor, остаток — крупнейшей ставке). Ничья /
  никто не угадал / все угадали / void → полный возврат всем.
- **Оффлайн-получатель** → отложенная выплата по образцу `EntryFeeForfeitStore` +
  `RefundForfeitIfStale` (Players.cs:164): стор uid → предметы, выдача в `OnReconnect`.
- **Анти-абьюз**: участники и матчи с ботами (`Bots.BotIdentity.IsBot` по ростеру) — без ставок;
  одна ставка на игрока на матч; лимит размера `betMaxStacks`; ставки на дуэли разрешены
  (главный сценарий), на рейтинг не влияют.

## Изменения — сервер

1. **`src/Scoring/BetBook.cs`** (новый): `JsonFileStore<Dictionary<long, List<BetRecord>>>`
   («bets.json»). `BetRecord { string Uid; int SideIndex; List<RewardItem> Items }`.
   API: `Place`, `ForMatch`, `RemoveMatch`, `HasBet`, `MatchIds()`. Плюс в том же файле
   `BetPayoutStore : JsonFileStore<Dictionary<string, List<RewardItem>>>` («betpayouts.json») —
   отложенные выдачи (выигрыш/возврат оффлайнику).
2. **`src/EventManagers/BetManager.cs`** (новый): оркестратор.
   - `PlaceBet(IServerPlayer, long matchId, int sideIndex, string itemsPacked)`:
     `spectatorBetsEnabled`; `FindRunningEvent`; `EventState != Running`; не участник
     (`re.IsParticipant`); нет ботов в ростере; `!HasBet`; `ItemSetUtil.Parse` + `Coalesce`,
     лимит `betMaxStacks`; `InventoryUtil.Has` → `Remove`; запись; ответ + рассылка сводки.
   - Подписки: `manager.OnMatchFinished` → `Settle(re, result)`;
     `manager.OnMatchAborted` → `RefundAll(re.Id)`.
   - `Settle`: победившие индексы сторон = группы из `re.ParticipantsByGroupsBefore`,
     пересекающиеся с `result.Winners`; пул = ставки остальных; пропорции по числу предметов;
     выдача `InventoryUtil.GiveByCode(..., "bet")` онлайн / `BetPayoutStore` оффлайн;
     `BetBook.RemoveMatch`.
   - Sweep осиротевших: на старте сервера (после инициализации менеджера) все `MatchIds()`, для
     которых `FindRunningEvent == null`, → возврат через `BetPayoutStore`.
   - Выдача отложенного: вызов из `CANEventManager.OnReconnect`
     (src/EventManagers/CANEventManager.Players.cs:141, рядом с `RefundForfeitIfStale`).
3. **`src/Config.cs`**: `spectatorBetsEnabled = true`, `betMaxStacks = 4`.
4. **`src/canevents.cs`**: создать BetManager после CANEventManager/NetworkHandler (рядом с
   DuelManager, ~строка 219), Dispose.

## Изменения — сеть

Паттерн: `Messages.Bets.cs` + `NetworkHandler.Bets.cs` (`RegisterBetHandlers()` из
NetworkHandler.cs:~103) + `ClientNetworkHandler.Bets.cs` (регистрация в конструкторе
ClientNetworkHandler.cs:~144). Новые типы — **строго append в конец** цепочки
CanEventsProtocol.cs (:141, после MatchReadyClosedMsg).

- `RequestBetInfoMsg { long EventId }` (c→s) — клиент открывает диалог ставки.
- `BetInfoMsg { EventId, GameModeCode, ArenaName, bool Open, string[] SideLabels
  (имена участников через manager.NameOf, «A, B» на сторону), string[] SideTotalsPacked
  (сумма ставок на сторону), string MyBetPacked, int MyBetSide }` (s→c) — снапшот в
  `ServerState<BetInfoMsg>` на клиенте (паттерн ServerState — ClientNetworkHandler.cs:43).
- `PlaceBetMsg { EventId, SideIndex, ItemsPacked }` (c→s) — ответ чат-строкой + свежий BetInfoMsg.
- `BetResultMsg` не нужен: итог — чат-сообщение при расчёте (`player.SendMessage`), суммы видно
  в BetInfoMsg до финиша.

## Изменения — клиент/GUI

1. **EventBrowserGui.Events.cs** (`AddEventCard`, :57): на карточке running-матча (EventId != 0,
   State != Running — окно открыто) второе действие «Ставка» → `RequestBetInfoMsg` и открытие
   диалога. (Карточка держит один onClick — добавить бейдж/вторую карточку-строку «Сделать
   ставку», по месту виднее; допустимо: клик по running-карточке открывает мини-меню
   Watch/Bet — решить при реализации по образцу существующего клика Watch.)
2. **`src/Gui/Native/BetPromptGui.cs`** (новый, по образцу DuelPromptGui/LoadoutPickGui):
   фокусируемый диалог: заголовок матча, по кнопке на сторону (SideLabels + текущие суммы),
   поле ставки («code:qty;…», как duelStake), кнопка «Поставить» → `PlaceBetMsg`. Показывает
   мою текущую ставку, если есть.
3. **SpectatorPanelGui.cs**: read-only строка в футере «Моя ставка: …» из
   `ServerState<BetInfoMsg>` (минимальная правка Compose/UpdateTexts) — опционально, в конце.
4. Лока en+ru: ~15 ключей (gui-bet-*, bet-placed/closed/won/refunded/errors).

## Порядок работ

1. `dotnet build` — добить компиляцию уже написанных фич сессии (h2h + мутаторы).
2. BetBook + BetPayoutStore → BetManager (+Config, canevents.cs) → сеть → GUI → лока → сборка.

## Проверка

- `dotnet build` без ошибок.
- Дымовой прогон (одиночный клиент + сервер): `/eventadmin start` матча с ботами → ставка должна
  быть отклонена; дуэль двух игроков (или solotest-конфиг) → третий игрок ставит из браузера,
  окно закрывается на Running, финиш → выплата/возврат в чате; рестарт сервера с открытой
  ставкой → возврат при реконнекте.
- Проверить, что EntryFee-взнос игрока и его ставка зрителя на другой матч сосуществуют.
