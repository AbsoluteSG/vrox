namespace Vrox.Equipment
{
    /// <summary>
    /// What kind of damage something deals.
    /// </summary>
    /// <remarks>
    /// Tallied separately per attacker, so a fight report can say where a
    /// player's damage came from rather than only how much there was. Must stay
    /// in step with the server's element ids.
    /// </remarks>
    public enum DamageElement : byte
    {
        Physical = 0,
        Fire = 1,
        Ice = 2,
        Poison = 3,
        Arcane = 4,
    }

    /// <summary>A lasting effect applied on hit.</summary>
    public enum DebuffKind : byte
    {
        None = 0,

        /// <summary>Cannot move or fire.</summary>
        Stun = 1,

        /// <summary>Moves at half speed.</summary>
        Slow = 2,

        /// <summary>
        /// Loses part of its defence, so later hits land harder.
        /// </summary>
        /// <remarks>
        /// Only meaningful against players — enemies subtract no flat defence, so
        /// on an enemy this applies and then changes nothing. It exists for boss
        /// weapons, where a weak bullet that makes the next one hurt is a
        /// different threat from a bullet that simply hurts.
        /// </remarks>
        ArmorBreak = 3,
    }
}
