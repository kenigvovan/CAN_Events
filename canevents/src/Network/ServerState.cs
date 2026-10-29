using System;

namespace canevents.src.Network
{
    // Client copy of the last message of one kind, plus the signal the GUI recomposes on.
    // Set() is the only writer, so value and event can't drift apart. Null until the server sends.
    public sealed class ServerState<T> where T : class
    {
        public T Value { get; private set; }

        // After every Set, even an equal payload: a fresh snapshot means "recompose", not "differs".
        public event Action Changed;

        public void Set(T value)
        {
            Value = value;
            Changed?.Invoke();
        }
    }
}
