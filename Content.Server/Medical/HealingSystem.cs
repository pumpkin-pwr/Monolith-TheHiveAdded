// SPDX-FileCopyrightText: 2022 DrSmugleaf
// SPDX-FileCopyrightText: 2022 Flipp Syder
// SPDX-FileCopyrightText: 2022 Leeroy
// SPDX-FileCopyrightText: 2022 Paul Ritter
// SPDX-FileCopyrightText: 2022 Rane
// SPDX-FileCopyrightText: 2022 metalgearsloth
// SPDX-FileCopyrightText: 2022 mirrorcult
// SPDX-FileCopyrightText: 2022 wrexbe
// SPDX-FileCopyrightText: 2023 Emisse
// SPDX-FileCopyrightText: 2023 Jezithyr
// SPDX-FileCopyrightText: 2023 Kara
// SPDX-FileCopyrightText: 2023 Leon Friedrich
// SPDX-FileCopyrightText: 2023 MisterMecky
// SPDX-FileCopyrightText: 2023 Nemanja
// SPDX-FileCopyrightText: 2023 OctoRocket
// SPDX-FileCopyrightText: 2023 Pieter-Jan Briers
// SPDX-FileCopyrightText: 2023 TemporalOroboros
// SPDX-FileCopyrightText: 2023 Vordenburg
// SPDX-FileCopyrightText: 2023 Whisper
// SPDX-FileCopyrightText: 2023 keronshb
// SPDX-FileCopyrightText: 2024 Cojoke
// SPDX-FileCopyrightText: 2024 LordCarve
// SPDX-FileCopyrightText: 2024 Plykiya
// SPDX-FileCopyrightText: 2024 nikthechampiongr
// SPDX-FileCopyrightText: 2025 Ark
// SPDX-FileCopyrightText: 2025 Dvir
// SPDX-FileCopyrightText: 2025 J
// SPDX-FileCopyrightText: 2025 SlamBamActionman
// SPDX-FileCopyrightText: 2025 Whatstone
// SPDX-FileCopyrightText: 2025 lzk
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Server.Administration.Logs;
using Content.Server.Body.Components;
using Content.Server.Body.Systems;
using Content.Server.Medical.Components;
using Content.Server.Popups;
using Content.Server.Stack;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.Damage;
using Content.Shared.Database;
using Content.Shared.DoAfter;
using Content.Shared.FixedPoint;
using Content.Shared.IdentityManagement;
using Content.Shared.Interaction;
using Content.Shared.Interaction.Events;
using Content.Shared.Medical;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Popups;
using Content.Shared.Stacks;
using Content.Shared.Body.Systems; // Shitmed Change
using Robust.Shared.Audio.Systems;
using Robust.Shared.Random;
using Robust.Shared.Audio;

// Shitmed Change
using Content.Shared._Shitmed.Targeting;
using Content.Shared.Body.Components;
using System.Linq;

namespace Content.Server.Medical;

public sealed class HealingSystem : EntitySystem
{
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly IAdminLogManager _adminLogger = default!;
    [Dependency] private readonly DamageableSystem _damageable = default!;
    [Dependency] private readonly SharedTargetingSystem _targetingSystem = default!; // Shitmed Change
    [Dependency] private readonly BloodstreamSystem _bloodstreamSystem = default!;
    [Dependency] private readonly SharedDoAfterSystem _doAfter = default!;
    [Dependency] private readonly StackSystem _stacks = default!;
    [Dependency] private readonly SharedInteractionSystem _interactionSystem = default!;
    [Dependency] private readonly MobThresholdSystem _mobThresholdSystem = default!;
    [Dependency] private readonly PopupSystem _popupSystem = default!;
    [Dependency] private readonly SharedSolutionContainerSystem _solutionContainerSystem = default!;
    [Dependency] private readonly SharedBodySystem _bodySystem = default!; // Shitmed Change

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<HealingComponent, UseInHandEvent>(OnHealingUse);
        SubscribeLocalEvent<HealingComponent, AfterInteractEvent>(OnHealingAfterInteract);
        SubscribeLocalEvent<DamageableComponent, HealingDoAfterEvent>(OnDoAfter);
    }

    private void OnDoAfter(Entity<DamageableComponent> entity, ref HealingDoAfterEvent args)
    {
        var dontRepeat = false;

        if (!TryComp(args.Used, out HealingComponent? healing))
            return;

        if (args.Handled || args.Cancelled)
            return;

        if (healing.DamageContainers is not null &&
            entity.Comp.DamageContainerID is not null &&
            !healing.DamageContainers.Contains(entity.Comp.DamageContainerID))
        {
            return;
        }

        // Heal some bloodloss damage.
        if (healing.BloodlossModifier != 0)
        {
            if (!TryComp<BloodstreamComponent>(entity.Owner, out var bloodstream))
                return;
            var isBleeding = bloodstream.BleedAmount > 0;
            _bloodstreamSystem.TryModifyBleedAmount(entity.Owner, healing.BloodlossModifier);
            if (isBleeding != bloodstream.BleedAmount > 0)
            {
                if (entity.Owner == args.User)
                    _popupSystem.PopupEntity(Loc.GetString("medical-item-stop-bleeding-self"), entity, args.User);
                else
                    _popupSystem.PopupEntity(Loc.GetString("medical-item-stop-bleeding", ("target", Identity.Entity(entity.Owner, EntityManager))), entity, args.User);
            }
        }

        // Restores missing blood
        if (healing.ModifyBloodLevel != 0)
            _bloodstreamSystem.TryModifyBloodLevel(entity.Owner, healing.ModifyBloodLevel);

        var healed = _damageable.TryChangeDamage(entity.Owner, healing.Damage, true, origin: args.User, canSever: false); // Shitmed Change

        if (healed == null && healing.BloodlossModifier != 0)
            return;

        var total = healed?.GetTotal() ?? FixedPoint2.Zero;

        // Re-verify that we can heal the damage.

        if (TryComp<StackComponent>(args.Used.Value, out var stackComp))
        {
            _stacks.Use(args.Used.Value, 1, stackComp);

            if (_stacks.GetCount(args.Used.Value, stackComp) <= 0)
                dontRepeat = true;
        }
        else
        {
            QueueDel(args.Used.Value);
        }

        if (entity.Owner != args.User)
        {
            _adminLogger.Add(LogType.Healed,
                $"{EntityManager.ToPrettyString(args.User):user} healed {EntityManager.ToPrettyString(entity.Owner):target} for {total:damage} damage");
        }
        else
        {
            _adminLogger.Add(LogType.Healed,
                $"{EntityManager.ToPrettyString(args.User):user} healed themselves for {total:damage} damage");
        }

        _audio.PlayPvs(healing.HealingEndSound, entity.Owner);

        // Logic to determine the whether or not to repeat the healing action
        args.Repeat = HasDamage(entity.Comp, healing) && !dontRepeat || IsPartDamaged(args.User, entity); // Shitmed Change
        if (!args.Repeat && !dontRepeat)
            _popupSystem.PopupEntity(Loc.GetString("medical-item-finished-using", ("item", args.Used)), entity.Owner, args.User);
        args.Handled = true;
    }

    private bool HasDamage(DamageableComponent component, HealingComponent healing)
    {
        var damageableDict = component.Damage.DamageDict;
        var healingDict = healing.Damage.DamageDict;
        foreach (var type in healingDict)
        {
            if (damageableDict[type.Key].Value > 0)
            {
                return true;
            }
        }

        return false;
    }

    // Shitmed Change Start
    private bool IsPartDamaged(EntityUid user, EntityUid target)
    {
        if (!TryComp(user, out TargetingComponent? targeting))
            return false;

        var (targetType, targetSymmetry) = _bodySystem.ConvertTargetBodyPart(targeting.Target);
        foreach (var part in _bodySystem.GetBodyChildrenOfType(target, targetType, symmetry: targetSymmetry))
            if (TryComp<DamageableComponent>(part.Id, out var damageable)
                && damageable.TotalDamage > part.Component.MinIntegrity)
                return true;

        return false;
    }

    // Shitmed Change End

    private void OnHealingUse(Entity<HealingComponent> entity, ref UseInHandEvent args)
    {
        if (args.Handled)
            return;

        if (TryHeal(entity, args.User, args.User, entity.Comp))
            args.Handled = true;
    }

    private void OnHealingAfterInteract(Entity<HealingComponent> entity, ref AfterInteractEvent args)
    {
        if (args.Handled || !args.CanReach || args.Target == null)
            return;

        if (TryHeal(entity, args.User, args.Target.Value, entity.Comp))
            args.Handled = true;
    }

    private bool TryHeal(EntityUid uid, EntityUid user, EntityUid target, HealingComponent component)
    {
        if (!TryComp<DamageableComponent>(target, out var targetDamage))
            return false;

        if (component.DamageContainers is not null &&
            targetDamage.DamageContainerID is not null &&
            !component.DamageContainers.Contains(targetDamage.DamageContainerID))
        {
            return false;
        }

        if (user != target && !_interactionSystem.InRangeUnobstructed(user, target, popup: true))
            return false;

        if (TryComp<StackComponent>(uid, out var stack) && stack.Count < 1)
            return false;

        var anythingToDo =
            HasDamage(targetDamage, component) ||
            IsPartDamaged(user, target) || // Shitmed Change
            component.ModifyBloodLevel > 0 // Special case if healing item can restore lost blood...
                && TryComp<BloodstreamComponent>(target, out var bloodstream)
                && _solutionContainerSystem.ResolveSolution(target, bloodstream.BloodSolutionName, ref bloodstream.BloodSolution, out var bloodSolution)
                && bloodSolution.Volume < bloodSolution.MaxVolume; // ...and there is lost blood to restore.

        if (!anythingToDo)
        {
            _popupSystem.PopupEntity(Loc.GetString("medical-item-cant-use", ("item", uid)), uid, user);
            return false;
        }

        _audio.PlayPvs(component.HealingBeginSound, uid);

        var isNotSelf = user != target;

        if (isNotSelf)
        {
            var msg = Loc.GetString("medical-item-popup-target", ("user", Identity.Entity(user, EntityManager)), ("item", uid));
            _popupSystem.PopupEntity(msg, target, target, PopupType.Medium);
        }

        var delay = isNotSelf
            ? component.Delay
            : component.Delay * GetScaledHealingPenalty(user, component);

        var doAfterEventArgs =
            new DoAfterArgs(EntityManager, user, delay, new HealingDoAfterEvent(), target, target: target, used: uid)
            {
                // Didn't break on damage as they may be trying to prevent it and
                // not being able to heal your own ticking damage would be frustrating.
                NeedHand = true,
                BreakOnMove = true,
                BreakOnWeightlessMove = false,
            };

        _doAfter.TryStartDoAfter(doAfterEventArgs);
        return true;
    }

    /// <summary>
    /// Scales the self-heal penalty based on the amount of damage taken
    /// </summary>
    /// <param name="uid"></param>
    /// <param name="component"></param>
    /// <returns></returns>
    public float GetScaledHealingPenalty(EntityUid uid, HealingComponent component)
    {
        var output = component.Delay;
        if (!TryComp<MobThresholdsComponent>(uid, out var mobThreshold) ||
            !TryComp<DamageableComponent>(uid, out var damageable))
            return output;
        if (!_mobThresholdSystem.TryGetThresholdForState(uid, MobState.Critical, out var amount, mobThreshold))
            return 1;

        var percentDamage = (float) (damageable.TotalDamage / amount);
        //basically make it scale from 1 to the multiplier.
        var modifier = percentDamage * (component.SelfHealPenaltyMultiplier - 1) + 1;
        return Math.Max(modifier, 1);
    }
}
