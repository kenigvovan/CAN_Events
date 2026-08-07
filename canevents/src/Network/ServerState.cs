using System;

namespace canevents.src.Network
{
    /// <summary>
    /// The client's copy of one server-fed snapshot: the last message of its kind, plus the "it
    /// changed, recompose" signal the GUI subscribes to. Set() is the only writer, so value and
    /// event cannot drift apart. Value stays null until the server has sent something.
    /// </summary>
    public sealed class ServerState<T> where T : class
    {
        public T Value { get; private set; }

        /// <summary>Raised after every Set - even when the payload is equal, because a fresh
        /// snapshot means "recompose from current data", not "the data differs".</summary>
        public event Action Changed;

        public void Set(T value)
        {
            Value = value;
            Changed?.Invoke();
        }
    }
}
