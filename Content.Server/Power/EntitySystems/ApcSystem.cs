using Content.Server.Emp;
using Content.Server.Popups;
using Content.Server.Power.Components;
using Content.Shared.Access.Components;
using Content.Shared.Access.Systems;
using Content.Shared.APC;
using Content.Shared.Emag.Components;
using Content.Shared.Emag.Systems;
using Content.Shared.Examine;
using Content.Shared.Interaction;
using Content.Shared.Popups;
using Content.Shared.Tools;
using Content.Shared.Tools.Components;
using JetBrains.Annotations;
using Robust.Server.GameObjects;
using Robust.Shared.Audio;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Server.Power.EntitySystems
{
    [UsedImplicitly]
    internal sealed class ApcSystem : EntitySystem
    {
        [Dependency] private readonly AccessReaderSystem _accessReader = default!;
        [Dependency] private readonly UserInterfaceSystem _userInterfaceSystem = default!;
        [Dependency] private readonly PopupSystem _popupSystem = default!;
        [Dependency] private readonly IGameTiming _gameTiming = default!;
        [Dependency] private readonly SharedToolSystem _toolSystem = default!;
        [Dependency] private readonly SharedAppearanceSystem _appearance = default!;

        private const float ScrewTime = 2f;

        public override void Initialize()
        {
            base.Initialize();

            UpdatesAfter.Add(typeof(PowerNetSystem));

            SubscribeLocalEvent<ApcComponent, MapInitEvent>(OnApcInit);
            SubscribeLocalEvent<ApcComponent, ChargeChangedEvent>(OnBatteryChargeChanged);
            SubscribeLocalEvent<ApcComponent, ApcToggleMainBreakerMessage>(OnToggleMainBreaker);
            SubscribeLocalEvent<ApcComponent, GotEmaggedEvent>(OnEmagged);

            SubscribeLocalEvent<ApcToolFinishedEvent>(OnToolFinished);
            SubscribeLocalEvent<ApcComponent, InteractUsingEvent>(OnInteractUsing);
            SubscribeLocalEvent<ApcComponent, ExaminedEvent>(OnExamine);

            SubscribeLocalEvent<ApcComponent, EmpPulseEvent>(OnEmpPulse);
        }

        // Change the APC's state only when the battery state changes, or when it's first created.
        private void OnBatteryChargeChanged(EntityUid uid, ApcComponent component, ChargeChangedEvent args)
        {
            UpdateApcState(uid, component);
        }

        private void OnApcInit(EntityUid uid, ApcComponent component, MapInitEvent args)
        {
            UpdateApcState(uid, component);
        }
        private void OnToggleMainBreaker(EntityUid uid, ApcComponent component, ApcToggleMainBreakerMessage args)
        {
            TryComp<AccessReaderComponent>(uid, out var access);
            if (args.Session.AttachedEntity == null)
                return;

            if (access == null || _accessReader.IsAllowed(args.Session.AttachedEntity.Value, access))
            {
                ApcToggleBreaker(uid, component);
            }
            else
            {
                _popupSystem.PopupCursor(Loc.GetString("apc-component-insufficient-access"),
                    args.Session, PopupType.Medium);
            }
        }

        public void ApcToggleBreaker(EntityUid uid, ApcComponent? apc = null, PowerNetworkBatteryComponent? battery = null)
        {
            if (!Resolve(uid, ref apc, ref battery))
                return;

            apc.MainBreakerEnabled = !apc.MainBreakerEnabled;
            battery.CanDischarge = apc.MainBreakerEnabled;

            UpdateUIState(uid, apc);
            SoundSystem.Play(apc.OnReceiveMessageSound.GetSound(), Filter.Pvs(uid), uid, AudioParams.Default.WithVolume(-2f));
        }

        private void OnEmagged(EntityUid uid, ApcComponent comp, ref GotEmaggedEvent args)
        {
            // no fancy conditions
            args.Handled = true;
        }

        public void UpdateApcState(EntityUid uid,
            ApcComponent? apc=null,
            BatteryComponent? battery=null)
        {
            if (!Resolve(uid, ref apc, ref battery))
                return;

            if (TryComp(uid, out AppearanceComponent? appearance))
            {
                UpdatePanelAppearance(uid, appearance, apc);
            }

            var newState = CalcChargeState(uid, apc, battery);
            if (newState != apc.LastChargeState && apc.LastChargeStateTime + ApcComponent.VisualsChangeDelay < _gameTiming.CurTime)
            {
                apc.LastChargeState = newState;
                apc.LastChargeStateTime = _gameTiming.CurTime;

                if (appearance != null)
                {
                    _appearance.SetData(uid, ApcVisuals.ChargeState, newState, appearance);
                }
            }

            var extPowerState = CalcExtPowerState(uid, apc, battery);
            if (extPowerState != apc.LastExternalState
                || apc.LastUiUpdate + ApcComponent.VisualsChangeDelay < _gameTiming.CurTime)
            {
                apc.LastExternalState = extPowerState;
                apc.LastUiUpdate = _gameTiming.CurTime;
                UpdateUIState(uid, apc, battery);
            }
        }

        public void UpdateUIState(EntityUid uid,
            ApcComponent? apc = null,
            BatteryComponent? battery = null,
            ServerUserInterfaceComponent? ui = null)
        {
            if (!Resolve(uid, ref apc, ref battery, ref ui))
                return;

            var netBattery = Comp<PowerNetworkBatteryComponent>(uid);
            float power = netBattery is not null ? netBattery.CurrentSupply : 0f;

            if (_userInterfaceSystem.GetUiOrNull(uid, ApcUiKey.Key, ui) is { } bui)
            {
                bui.SetState(new ApcBoundInterfaceState(apc.MainBreakerEnabled, (int)MathF.Ceiling(power), apc.LastExternalState, battery.CurrentCharge / battery.MaxCharge));
            }
        }

        public ApcChargeState CalcChargeState(EntityUid uid,
            ApcComponent? apc=null,
            BatteryComponent? battery=null)
        {
            if (apc != null && HasComp<EmaggedComponent>(uid))
                return ApcChargeState.Emag;

            if (!Resolve(uid, ref apc, ref battery))
                return ApcChargeState.Lack;

            var chargeFraction = battery.CurrentCharge / battery.MaxCharge;

            if (chargeFraction > ApcComponent.HighPowerThreshold)
            {
                return ApcChargeState.Full;
            }

            var netBattery = Comp<PowerNetworkBatteryComponent>(uid);
            var delta = netBattery.CurrentSupply - netBattery.CurrentReceiving;

            return delta < 0 ? ApcChargeState.Charging : ApcChargeState.Lack;
        }

        public ApcExternalPowerState CalcExtPowerState(EntityUid uid,
            ApcComponent? apc=null,
            BatteryComponent? battery=null)
        {
            if (!Resolve(uid, ref apc, ref battery))
                return ApcExternalPowerState.None;

            var netBat = Comp<PowerNetworkBatteryComponent>(uid);
            if (netBat.CurrentReceiving == 0 && !MathHelper.CloseTo(battery.CurrentCharge / battery.MaxCharge, 1))
            {
                return ApcExternalPowerState.None;
            }

            var delta = netBat.CurrentReceiving - netBat.CurrentSupply;
            if (!MathHelper.CloseToPercent(delta, 0, 0.1f) && delta < 0)
            {
                return ApcExternalPowerState.Low;
            }

            return ApcExternalPowerState.Good;
        }

        public static ApcPanelState GetPanelState(ApcComponent apc)
        {
            if (apc.IsApcOpen)
                return ApcPanelState.Open;
            else
                return ApcPanelState.Closed;
        }

        private void OnInteractUsing(EntityUid uid, ApcComponent component, InteractUsingEvent args)
        {
            if (!EntityManager.TryGetComponent(args.Used, out ToolComponent? tool))
                return;

            var toolEvData = new ToolEventData(new ApcToolFinishedEvent(uid), fuel: 0f);

            if (_toolSystem.UseTool(args.Used, args.User, uid, ScrewTime, new [] { "Screwing" }, toolEvData, toolComponent:tool))
                args.Handled = true;
        }

        private void OnToolFinished(ApcToolFinishedEvent args)
        {
            if (!EntityManager.TryGetComponent(args.Target, out ApcComponent? component))
                return;
            component.IsApcOpen = !component.IsApcOpen;

            if (TryComp(args.Target, out AppearanceComponent? appearance))
            {
                UpdatePanelAppearance(args.Target, appearance);
            }

            if (component.IsApcOpen)
                SoundSystem.Play(component.ScrewdriverOpenSound.GetSound(), Filter.Pvs(args.Target), args.Target);
            else
                SoundSystem.Play(component.ScrewdriverCloseSound.GetSound(), Filter.Pvs(args.Target), args.Target);
        }

        private void UpdatePanelAppearance(EntityUid uid, AppearanceComponent? appearance = null, ApcComponent? apc = null)
        {
            if (!Resolve(uid, ref appearance, ref apc, false))
                return;

            _appearance.SetData(uid, ApcVisuals.PanelState, GetPanelState(apc), appearance);
        }

        private sealed class ApcToolFinishedEvent : EntityEventArgs
        {
            public EntityUid Target { get; }

            public ApcToolFinishedEvent(EntityUid target)
            {
                Target = target;
            }
        }

        private void OnExamine(EntityUid uid, ApcComponent component, ExaminedEvent args)
        {
            args.PushMarkup(Loc.GetString(component.IsApcOpen
                ? "apc-component-on-examine-panel-open"
                : "apc-component-on-examine-panel-closed"));
        }

        private void OnEmpPulse(EntityUid uid, ApcComponent component, ref EmpPulseEvent args)
        {
            if (component.MainBreakerEnabled)
            {
                args.Affected = true;
                ApcToggleBreaker(uid, component);
            }
        }
    }
}
