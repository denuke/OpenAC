namespace AcDream.Core.Items;

public static class PaperdollSelectionPolicy
{
    public static uint GetUpperInventoryObject(
        ClientObjectTable objects,
        uint playerId,
        EquipMask bodyLocationMask)
    {
        if (playerId == 0 || bodyLocationMask == EquipMask.None || objects.Get(playerId) is null)
            return 0;

        ClientObject? winner = null;
        foreach (ClientObject candidate in objects.Objects)
        {
            if ((candidate.CurrentlyEquippedLocation & bodyLocationMask) == EquipMask.None)
                continue;
            if (candidate.WielderId != playerId && candidate.ContainerId != playerId)
                continue;

            if (winner is null
                || EffectivePriority(candidate, bodyLocationMask)
                    > EffectivePriority(winner, bodyLocationMask))
                winner = candidate;
        }

        return winner?.ObjectId ?? playerId;
    }

    /// <summary>The outermost worn object at each of several body locations, in
    /// one pass over the player's equipment rather than one pass per location.
    /// Each answer is what <see cref="GetUpperInventoryObject"/> would give for
    /// that location on its own.</summary>
    public static void GetUpperInventoryObjects(
        ClientObjectTable objects,
        uint playerId,
        ReadOnlySpan<EquipMask> bodyLocationMasks,
        Span<uint> results)
    {
        if (results.Length < bodyLocationMasks.Length)
            throw new ArgumentException("One result per location is required.", nameof(results));

        results[..bodyLocationMasks.Length].Clear();
        if (playerId == 0 || objects.Get(playerId) is null)
            return;

        int count = bodyLocationMasks.Length;
        Span<uint> winnerId = count <= 16 ? stackalloc uint[16] : new uint[count];
        Span<uint> winnerPriority = count <= 16 ? stackalloc uint[16] : new uint[count];
        winnerId = winnerId[..count];
        winnerPriority = winnerPriority[..count];
        winnerId.Clear();
        winnerPriority.Clear();

        foreach (ClientObject candidate in objects.Objects)
        {
            if (candidate.CurrentlyEquippedLocation == EquipMask.None)
                continue;
            if (candidate.WielderId != playerId && candidate.ContainerId != playerId)
                continue;

            for (int i = 0; i < count; i++)
            {
                EquipMask location = bodyLocationMasks[i];
                if (location == EquipMask.None
                    || (candidate.CurrentlyEquippedLocation & location) == EquipMask.None)
                    continue;

                uint priority = EffectivePriority(candidate, location);
                // First one in wins a tie, exactly as the single-location scan does.
                if (winnerId[i] == 0u || priority > winnerPriority[i])
                {
                    winnerId[i] = candidate.ObjectId;
                    winnerPriority[i] = priority;
                }
            }
        }

        for (int i = 0; i < count; i++)
        {
            results[i] = bodyLocationMasks[i] == EquipMask.None
                ? 0u
                : winnerId[i] != 0u ? winnerId[i] : playerId;
        }
    }

    private static uint EffectivePriority(ClientObject item, EquipMask bodyLocationMask)
    {
        uint priority = item.Priority;
        uint coveredLocation = (uint)(item.CurrentlyEquippedLocation & bodyLocationMask);
        if (priority == 0 && coveredLocation is >= 0x200u and <= 0x4000u)
            priority = 0x7Fu;
        return priority;
    }
}
