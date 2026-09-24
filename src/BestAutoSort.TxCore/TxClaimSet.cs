using System.Collections.Generic;

namespace BestAutoSort.TxCore
{
    /// <summary>
    /// At-most-once submit lease for in-flight Adds, shared by production
    /// (ChestTxService.InFlightAdds, keyed by live ItemData) and the offline
    /// suite. The SAME live item must never be submitted twice (quick-stack
    /// snapshots every chest from one inventory state; a double click re-sends
    /// the same stack): the first submit claims it, later duplicates are pruned.
    ///
    /// Lease discipline (enforced by the caller's try/finally, pinned by tests):
    /// claims are held only until the Pending map (remote) or the synchronous
    /// local drain owns them; EVERY pre-ownership failure path (counter
    /// exhausted, encode failure, invalid netview, insert failure, destroyed
    /// container) releases what it claimed. Terminal completion releases the
    /// claim before its single callback attempt; Release is idempotent so a
    /// double-finalize can never strand or double-free a claim. This is
    /// at-most-once SUBMIT with an exactly-once callback attempt — NOT
    /// end-to-end exactly-once (no ACK/journal; committed-but-Indeterminate
    /// and client-crash windows stay residual).
    /// </summary>
    public sealed class TxClaimSet<T> where T : class
    {
        private readonly HashSet<T> _held = new HashSet<T>();

        /// <summary>Claims an item for flight. False = already in flight
        /// (caller must prune it, never submit it twice).</summary>
        public bool TryClaim(T item)
        {
            if (item == null)
                return false;
            return _held.Add(item);
        }

        /// <summary>Releases one claim. Idempotent: unknown items are ignored.</summary>
        public void Release(T item)
        {
            if (item == null)
                return;
            _held.Remove(item);
        }

        /// <summary>Releases a batch of claims (terminal completion or a failed
        /// submit that never reached Pending). Idempotent, never throws.</summary>
        public void ReleaseAll(IEnumerable<T> items)
        {
            if (items == null)
                return;
            foreach (T item in items)
            {
                if (item != null)
                    _held.Remove(item);
            }
        }

        public int Count
        {
            get { return _held.Count; }
        }

        public bool IsClaimed(T item)
        {
            if (item == null)
                return false;
            return _held.Contains(item);
        }
    }
}
