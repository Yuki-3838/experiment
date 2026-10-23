using System.Collections.Generic;
using UnityEngine;

namespace Party1111111
{
    public sealed class Party1111111Character : ComCharacterBase
    {
        protected override int SXG_ChooseAttackTargetIndex(IReadOnlyList<VisibleCharacterData> candidates)
        {
            if (SXG_HasTreasure || SXG_IsKnockedOut || SXG_IsActionLocked) return -1;
            int best = -1;
            float distance = float.MaxValue;
            for (int i = 0; i < candidates.Count; i++)
            {
                VisibleCharacterData enemy = candidates[i];
                if (enemy.IsKnockedOut || !enemy.HasTreasure) continue;
                float d = (enemy.WorldPosition - SXG_WorldPosition).sqrMagnitude;
                if (d < distance) { distance = d; best = i; }
            }
            return best;
        }
        protected override bool SXG_ShouldPickUpTreasure(TreasureType type) => !SXG_HasTreasure;
    }
}
