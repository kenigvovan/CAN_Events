# Ответ на вопрос «как растянуть или отключить таймер набора на событие»

> Прежняя версия этого файла утверждала, что механики окна регистрации в моде нет.
> Это было верно до появления `src/EventManagers/SignupWindows.cs`. Теперь фича есть,
> и ответ ниже — по ней.

## Суть

Человек ищет настройку в конфиге, а это не настройка, а админская команда:
`/eventadmin open <code> [minutes]` (`src/commands/CommandsRegister.cs:172`).

Что она делает (`src/EventManagers/SignupWindows.cs:139`):

- открывает окно набора на N минут, всем онлайн уходит строка в чат и панель в углу
  с обратным отсчётом, счётчиком записавшихся и клавишей для входа;
- **удерживает** старт: пока окно открыто, минимум режима (3 для Hunger Games) матч не запускает;
- матч стартует по истечении времени, при заполнении лобби, или по `/eventadmin start`.

`minutes = 0` — окно без часов, ждёт ручного старта. Это ровно тот сценарий «анонсировал,
дал людям собраться, нажал go», который человек описывает.

Если число опущено — берётся `signupWindowMinutes` из конфига, по умолчанию 5
(`src/commands/CommandsImplementations.cs:251`, `src/Config.cs:27`). Потолок — 24 часа.
Отрицательное число отвергается, а не приводится к нулю, чтобы опечатка `-5` не выглядела
как сработавшая команда.

Голосовой чат при этом не нужен вообще, и никому не надо ничего печатать: панель показывает
реальную привязку клавиши (по умолчанию **G**), а **Shift+G** прячет её.

## Текст ответа (на английском, как задан вопрос)

You didn't miss a config option - it isn't a config field, it's an admin command:

```
/eventadmin open hungergames 5
```

That opens a **registration window** for five minutes. During those five minutes the match is
held back no matter how many people sign up, and every player online gets a chat line plus a
panel in the corner with a live countdown, the sign-up count, and the key to join with
(default **G**, rebindable in Settings -> Controls). Nobody needs to be in voice chat, and
nobody has to type anything.

Two things it does that plain `/canreg` cannot:

- The mode's minimum (3 for Hunger Games) **cannot** start the match early. It starts when the
  timer runs out, when the lobby fills to the maximum, or when you say so - never before.
- `/eventadmin open hungergames 0` opens a window with **no clock at all**. It stays open until
  you run `/eventadmin start hungergames` yourself, and the panel says "starts when the admin
  says so" instead of counting down. That is exactly your "announce it, wait for people, then
  press go" workflow.

Supporting commands:

- `/eventadmin status` - which windows are open, who signed up, how long is left.
- `/eventadmin cancel hungergames` - close the window without starting anything.
- Omitting the number uses `signupWindowMinutes` from `canevents.json` (default 5).

Players who find the panel intrusive can shrink it to a single line or turn it off entirely
with `/canhud invite mini` / `/canhud invite off` - the join key keeps working either way.
The event browser (**K**) also carries the open window: that event is sorted to the top,
highlighted, with the same countdown.

One older, config-only knob exists and is worth knowing about, but it solves less than half of
your problem: `queueFillSeconds` in the event's `FullPartyRequired` block makes the queue wait
N seconds **after the minimum is reached**. It announces nothing, and the countdown only starts
from the 3rd sign-up, so latecomers still have no idea anything is happening. The registration
window is the intended answer.
