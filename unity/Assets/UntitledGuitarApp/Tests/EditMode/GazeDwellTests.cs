// EditMode tests for gaze-dwell (SL0 / SL8 hands-free input): every DwellTimer transition, the hysteresis, the cooldown,
// the GazeMath angle thresholds and nearest-target selection, and the pure reticle mesh builder.
// No scene, no Play Mode, no clock, no sleeps: time is whatever each test passes to Tick.
using NUnit.Framework;
using UnityEngine;
using UntitledGuitarApp.Runtime.Gaze;

namespace UntitledGuitarApp.Tests.EditMode
{
    [TestFixture]
    public class GazeDwellTests
    {
        // Binary-exact numbers (0.25 s steps, 1.0 s dwell, 1.0 s cooldown) so float rounding cannot hide a bug.
        // The rounding case has its own test below.
        const float Dt = 0.25f;
        const float Enter = 3f;
        const float Exit = 4f;
        const float Inside = 0f;       // a gaze dead on the target
        const float Away = 90f;        // a gaze far from the target

        static DwellTimer NewTimer(float dwell = 1f, float cooldown = 1f)
        {
            return new DwellTimer(Enter, Exit, dwell, cooldown);
        }

        /// <summary>A timer that has just entered a dwell (progress 0).</summary>
        static DwellTimer Dwelling(float dwell = 1f, float cooldown = 1f)
        {
            DwellTimer t = NewTimer(dwell, cooldown);
            Assert.IsFalse(t.Tick(Inside, Dt));
            Assert.AreEqual(DwellState.Dwelling, t.State);
            return t;
        }

        /// <summary>A timer that has just completed a dwell (State == Fired).</summary>
        static DwellTimer Fired(float cooldown = 1f)
        {
            DwellTimer t = Dwelling(1f, cooldown);
            for (int i = 0; i < 3; i++) Assert.IsFalse(t.Tick(Inside, Dt));
            Assert.IsTrue(t.Tick(Inside, Dt));
            Assert.AreEqual(DwellState.Fired, t.State);
            return t;
        }

        // ------------------------------------------------------------------------------------------------ DwellTimer

        [Test]
        public void New_timer_is_idle_armed_and_has_the_documented_defaults()
        {
            var t = new DwellTimer();
            Assert.AreEqual(DwellState.Idle, t.State);
            Assert.IsTrue(t.IsArmed);
            Assert.AreEqual(0f, t.Progress);
            Assert.AreEqual(0f, t.CooldownRemaining);
            Assert.AreEqual(1.2f, t.DwellSeconds);
            Assert.AreEqual(1.0f, t.CooldownSeconds);
            Assert.Less(t.EnterAngleDeg, t.ExitAngleDeg, "the default hysteresis must be wider to stay than to start");
        }

        [Test]
        public void Idle_stays_idle_while_the_gaze_is_outside_the_enter_angle()
        {
            DwellTimer t = NewTimer();
            for (int i = 0; i < 10; i++)
            {
                Assert.IsFalse(t.Tick(Away, Dt));
                Assert.AreEqual(DwellState.Idle, t.State);
                Assert.AreEqual(0f, t.Progress);
            }
        }

        [Test]
        public void Idle_to_dwelling_happens_at_exactly_the_enter_angle()
        {
            DwellTimer t = NewTimer();
            Assert.IsFalse(t.Tick(Enter, Dt));
            Assert.AreEqual(DwellState.Dwelling, t.State, "the enter angle itself counts as inside");
        }

        [Test]
        public void Idle_just_beyond_the_enter_angle_does_not_start()
        {
            DwellTimer t = NewTimer();
            t.Tick(Enter + 0.001f, Dt);
            Assert.AreEqual(DwellState.Idle, t.State);
        }

        [Test]
        public void The_tick_that_enters_a_dwell_does_not_count_as_held_time()
        {
            DwellTimer t = Dwelling();
            Assert.AreEqual(0f, t.Progress);
        }

        [Test]
        public void Dwelling_progress_follows_the_time_passed_in()
        {
            DwellTimer t = Dwelling();
            t.Tick(Inside, Dt); Assert.AreEqual(0.25f, t.Progress, 1e-6f);
            t.Tick(Inside, Dt); Assert.AreEqual(0.50f, t.Progress, 1e-6f);
            t.Tick(Inside, Dt); Assert.AreEqual(0.75f, t.Progress, 1e-6f);
            Assert.AreEqual(DwellState.Dwelling, t.State);
        }

        [Test]
        public void Dwelling_to_fired_after_the_dwell_time_and_tick_reports_it_exactly_once()
        {
            DwellTimer t = Dwelling();
            int fires = 0;
            for (int i = 0; i < 3; i++) if (t.Tick(Inside, Dt)) fires++;
            Assert.AreEqual(0, fires, "not fired before the dwell time has passed");
            Assert.IsTrue(t.Tick(Inside, Dt), "fires on the tick that completes the dwell");
            Assert.AreEqual(DwellState.Fired, t.State);
            Assert.AreEqual(1f, t.Progress);
            Assert.IsFalse(t.IsArmed);
        }

        [Test]
        public void Fired_lasts_one_tick_then_the_cooldown_starts_and_uses_that_ticks_time()
        {
            DwellTimer t = Fired();
            Assert.IsFalse(t.Tick(Inside, Dt), "a fire is reported once");
            Assert.AreEqual(DwellState.Cooldown, t.State);
            Assert.AreEqual(0.75f, t.CooldownRemaining, 1e-6f);
            Assert.AreEqual(0f, t.Progress);
            Assert.IsFalse(t.IsArmed);
        }

        [Test]
        public void Cooldown_blocks_a_new_dwell_while_the_gaze_stays_on_the_target()
        {
            DwellTimer t = Fired();
            for (int i = 0; i < 3; i++)
            {
                Assert.IsFalse(t.Tick(Inside, Dt));
                Assert.AreEqual(DwellState.Cooldown, t.State, "tick " + i);
                Assert.AreEqual(0f, t.Progress);
            }
        }

        [Test]
        public void Cooldown_counts_down_to_idle_when_the_gaze_is_elsewhere()
        {
            DwellTimer t = Fired();
            t.Tick(Away, Dt); Assert.AreEqual(0.75f, t.CooldownRemaining, 1e-6f);
            t.Tick(Away, Dt); Assert.AreEqual(0.50f, t.CooldownRemaining, 1e-6f);
            t.Tick(Away, Dt); Assert.AreEqual(0.25f, t.CooldownRemaining, 1e-6f);
            t.Tick(Away, Dt);
            Assert.AreEqual(DwellState.Idle, t.State);
            Assert.AreEqual(0f, t.CooldownRemaining);
            Assert.IsTrue(t.IsArmed);
        }

        [Test]
        public void A_new_dwell_can_begin_on_the_tick_the_cooldown_ends()
        {
            DwellTimer t = Fired();
            for (int i = 0; i < 3; i++) t.Tick(Inside, Dt);          // still in cooldown
            t.Tick(Inside, Dt);                                       // cooldown runs out here
            Assert.AreEqual(DwellState.Dwelling, t.State);
            Assert.AreEqual(0f, t.Progress);
        }

        [Test]
        public void A_held_gaze_fires_again_every_dwell_plus_cooldown_seconds()
        {
            DwellTimer t = NewTimer(1f, 1f);
            int tick = 0, first = -1, second = -1, third = -1;
            for (; tick < 40; tick++)
            {
                if (!t.Tick(Inside, Dt)) continue;
                if (first < 0) first = tick; else if (second < 0) second = tick; else { third = tick; break; }
            }
            Assert.AreEqual(4, first, "1.0 s after the entering tick");
            Assert.AreEqual(8, second - first, "dwell 1.0 s + cooldown 1.0 s = 8 ticks of 0.25 s");
            Assert.AreEqual(8, third - second);
        }

        [Test]
        public void Hysteresis_a_gaze_between_the_enter_and_exit_angle_cannot_start_a_dwell()
        {
            DwellTimer t = NewTimer();
            float between = (Enter + Exit) / 2f;
            for (int i = 0; i < 6; i++) t.Tick(between, Dt);
            Assert.AreEqual(DwellState.Idle, t.State);
        }

        [Test]
        public void Hysteresis_a_gaze_between_the_enter_and_exit_angle_keeps_a_running_dwell_going()
        {
            DwellTimer t = Dwelling();
            float between = (Enter + Exit) / 2f;
            t.Tick(between, Dt); Assert.AreEqual(DwellState.Dwelling, t.State);
            t.Tick(between, Dt); Assert.AreEqual(0.5f, t.Progress, 1e-6f);
            t.Tick(between, Dt); Assert.AreEqual(0.75f, t.Progress, 1e-6f);
            Assert.IsTrue(t.Tick(between, Dt), "the dwell completes even though the gaze never came back inside the enter angle");
        }

        [Test]
        public void Hysteresis_the_exit_angle_itself_still_counts_as_inside()
        {
            DwellTimer t = Dwelling();
            t.Tick(Exit, Dt);
            Assert.AreEqual(DwellState.Dwelling, t.State);
        }

        [Test]
        public void Dwelling_to_idle_when_the_gaze_leaves_beyond_the_exit_angle_and_progress_is_lost()
        {
            DwellTimer t = Dwelling();
            t.Tick(Inside, Dt); t.Tick(Inside, Dt);
            Assert.AreEqual(0.5f, t.Progress, 1e-6f);

            Assert.IsFalse(t.Tick(Exit + 0.001f, Dt));
            Assert.AreEqual(DwellState.Idle, t.State);
            Assert.AreEqual(0f, t.Progress);
        }

        [Test]
        public void After_a_broken_dwell_the_next_one_starts_from_zero()
        {
            DwellTimer t = Dwelling();
            t.Tick(Inside, Dt); t.Tick(Inside, Dt); t.Tick(Inside, Dt);   // 0.75 held
            t.Tick(Away, Dt);                                              // broken
            t.Tick(Inside, Dt);                                            // re-enter
            Assert.AreEqual(DwellState.Dwelling, t.State);
            Assert.AreEqual(0f, t.Progress);
            for (int i = 0; i < 3; i++) Assert.IsFalse(t.Tick(Inside, Dt));
            Assert.IsTrue(t.Tick(Inside, Dt), "needs a full dwell again");
        }

        [Test]
        public void An_exit_angle_below_the_enter_angle_is_treated_as_the_enter_angle()
        {
            var t = new DwellTimer(Enter, 1f, 1f, 1f);   // misconfigured
            t.Tick(Enter - 0.1f, Dt);
            Assert.AreEqual(DwellState.Dwelling, t.State);
            t.Tick(Enter - 0.1f, Dt);
            Assert.AreEqual(DwellState.Dwelling, t.State, "must not exit while still inside the enter angle");
        }

        [Test]
        public void Zero_cooldown_goes_from_fired_straight_back_to_idle()
        {
            DwellTimer t = Fired(0f);
            t.Tick(Away, Dt);
            Assert.AreEqual(DwellState.Idle, t.State);
            Assert.IsTrue(t.IsArmed);
        }

        [Test]
        public void Zero_cooldown_with_the_gaze_still_inside_starts_the_next_dwell_at_once()
        {
            DwellTimer t = Fired(0f);
            t.Tick(Inside, Dt);
            Assert.AreEqual(DwellState.Dwelling, t.State);
            Assert.AreEqual(0f, t.Progress);
        }

        [Test]
        public void ForceFire_from_idle_fires_at_once_then_enters_cooldown()
        {
            DwellTimer t = NewTimer();
            Assert.IsTrue(t.ForceFire());
            Assert.AreEqual(DwellState.Fired, t.State);
            Assert.AreEqual(1f, t.Progress);
            t.Tick(Away, Dt);
            Assert.AreEqual(DwellState.Cooldown, t.State);
        }

        [Test]
        public void ForceFire_from_dwelling_fires_at_once()
        {
            DwellTimer t = Dwelling();
            t.Tick(Inside, Dt);
            Assert.IsTrue(t.ForceFire());
            Assert.AreEqual(DwellState.Fired, t.State);
        }

        [Test]
        public void ForceFire_is_ignored_in_fired_and_cooldown_so_a_press_cannot_fire_twice()
        {
            DwellTimer t = Fired();
            Assert.IsFalse(t.ForceFire(), "already fired");
            Assert.AreEqual(DwellState.Fired, t.State);

            t.Tick(Away, Dt);
            Assert.AreEqual(DwellState.Cooldown, t.State);
            Assert.IsFalse(t.ForceFire(), "locked out");
            Assert.AreEqual(DwellState.Cooldown, t.State);
        }

        [Test]
        public void Reset_returns_to_idle_from_every_state()
        {
            DwellTimer[] timers =
            {
                NewTimer(),                 // Idle
                Dwelling(),                 // Dwelling
                Fired(),                    // Fired
                CooledDown(),               // Cooldown
            };
            DwellState[] expected = { DwellState.Idle, DwellState.Dwelling, DwellState.Fired, DwellState.Cooldown };
            for (int i = 0; i < timers.Length; i++)
            {
                Assert.AreEqual(expected[i], timers[i].State, "setup " + i);
                timers[i].Reset();
                Assert.AreEqual(DwellState.Idle, timers[i].State, "after reset " + i);
                Assert.AreEqual(0f, timers[i].Progress);
                Assert.AreEqual(0f, timers[i].CooldownRemaining);
                Assert.IsTrue(timers[i].IsArmed);
            }
        }

        static DwellTimer CooledDown()
        {
            DwellTimer t = Fired();
            t.Tick(Away, Dt);
            Assert.AreEqual(DwellState.Cooldown, t.State);
            return t;
        }

        [Test]
        public void A_huge_time_step_after_entering_fires_in_one_tick()
        {
            DwellTimer t = Dwelling();
            Assert.IsTrue(t.Tick(Inside, 100f));
            Assert.AreEqual(DwellState.Fired, t.State);
        }

        [Test]
        public void A_huge_time_step_ends_a_cooldown_in_one_tick()
        {
            DwellTimer t = Fired();
            t.Tick(Away, 100f);
            Assert.AreEqual(DwellState.Idle, t.State);
        }

        [Test]
        public void Negative_and_NaN_time_steps_are_ignored()
        {
            DwellTimer t = Dwelling();
            t.Tick(Inside, Dt);
            Assert.AreEqual(0.25f, t.Progress, 1e-6f);
            t.Tick(Inside, -5f);
            Assert.AreEqual(0.25f, t.Progress, 1e-6f);
            t.Tick(Inside, float.NaN);
            Assert.AreEqual(0.25f, t.Progress, 1e-6f);
            Assert.AreEqual(DwellState.Dwelling, t.State);
        }

        [Test]
        public void A_NaN_angle_counts_as_outside()
        {
            DwellTimer idle = NewTimer();
            idle.Tick(float.NaN, Dt);
            Assert.AreEqual(DwellState.Idle, idle.State);

            DwellTimer dwelling = Dwelling();
            dwelling.Tick(float.NaN, Dt);
            Assert.AreEqual(DwellState.Idle, dwelling.State);
        }

        [Test]
        public void An_infinite_angle_is_how_a_target_without_gaze_is_released()
        {
            DwellTimer t = Dwelling();
            t.Tick(Inside, Dt);
            t.Tick(float.PositiveInfinity, 0f);
            Assert.AreEqual(DwellState.Idle, t.State);
            Assert.AreEqual(0f, t.Progress);
        }

        [Test]
        public void A_zero_dwell_time_fires_on_the_first_tick_after_entering()
        {
            DwellTimer t = NewTimer(0f, 1f);
            Assert.IsFalse(t.Tick(Inside, Dt));
            Assert.AreEqual(DwellState.Dwelling, t.State);
            Assert.AreEqual(0f, t.Progress);
            Assert.IsTrue(t.Tick(Inside, Dt));
        }

        [Test]
        public void The_default_dwell_fires_on_exactly_the_72nd_frame_after_entering_at_60_fps()
        {
            // 72 x (1/60 s) is 1.1999996 in float32, just under 1.2. Without the float tolerance this fires on frame 73.
            var t = new DwellTimer();
            Assert.AreEqual(1.2f, t.DwellSeconds);
            const float frame = 1f / 60f;
            Assert.IsFalse(t.Tick(0f, frame), "entering tick");
            for (int i = 1; i <= 71; i++) Assert.IsFalse(t.Tick(0f, frame), "frame " + i);
            Assert.IsTrue(t.Tick(0f, frame), "frame 72 = 1.2 s held");
        }

        [Test]
        public void The_default_dwell_fires_on_exactly_the_120th_tick_after_entering_at_100_hz()
        {
            // 120 x 0.01 s is 1.1999992 in float32.
            var t = new DwellTimer();
            Assert.IsFalse(t.Tick(0f, 0.01f), "entering tick");
            for (int i = 1; i <= 119; i++) Assert.IsFalse(t.Tick(0f, 0.01f), "tick " + i);
            Assert.IsTrue(t.Tick(0f, 0.01f), "tick 120 = 1.2 s held");
        }

        [Test]
        public void Progress_always_stays_between_zero_and_one()
        {
            DwellTimer t = NewTimer();
            for (int i = 0; i < 60; i++)
            {
                t.Tick(i % 17 == 0 ? Away : Inside, 0.07f);
                Assert.GreaterOrEqual(t.Progress, 0f);
                Assert.LessOrEqual(t.Progress, 1f);
            }
        }

        // ------------------------------------------------------------------------------------------------ GazeMath

        [TestCase(0f, 0f, 1f, 0f, 0f, 1f, 0f)]
        [TestCase(0f, 0f, 1f, 1f, 0f, 0f, 90f)]
        [TestCase(0f, 0f, 1f, 0f, 0f, -1f, 180f)]
        [TestCase(0f, 0f, 1f, 1f, 0f, 1f, 45f)]
        [TestCase(0f, 0f, 1f, 0f, 1f, 1f, 45f)]
        [TestCase(1f, 0f, 0f, 0f, 1f, 0f, 90f)]
        [TestCase(0f, 0f, 1f, 1f, 0f, 1.7320508f, 30f)]
        public void AngleBetweenDeg_gives_the_known_angle(float ax, float ay, float az, float bx, float by, float bz, float expectedDeg)
        {
            Assert.AreEqual(expectedDeg, GazeMath.AngleBetweenDeg(new Vector3(ax, ay, az), new Vector3(bx, by, bz)), 1e-3f);
        }

        [Test]
        public void AngleBetweenDeg_ignores_vector_length_and_argument_order()
        {
            Vector3 a = new Vector3(0f, 0f, 5f);
            Vector3 b = new Vector3(100f, 0f, 100f);
            Assert.AreEqual(45f, GazeMath.AngleBetweenDeg(a, b), 1e-3f);
            Assert.AreEqual(45f, GazeMath.AngleBetweenDeg(b, a), 1e-3f);
        }

        [TestCase(0.01f)]
        [TestCase(0.1f)]
        [TestCase(0.5f)]
        public void AngleBetweenDeg_is_accurate_for_the_tiny_angles_dwell_targets_live_at(float deg)
        {
            Vector3 forward = Vector3.forward;
            Vector3 toTarget = new Vector3(Mathf.Tan(deg * Mathf.Deg2Rad), 0f, 1f);
            Assert.AreEqual(deg, GazeMath.AngleBetweenDeg(forward, toTarget), deg * 1e-3f);
        }

        [Test]
        public void AngleBetweenDeg_is_infinity_for_a_zero_or_NaN_direction()
        {
            Assert.IsTrue(float.IsPositiveInfinity(GazeMath.AngleBetweenDeg(Vector3.zero, Vector3.forward)));
            Assert.IsTrue(float.IsPositiveInfinity(GazeMath.AngleBetweenDeg(Vector3.forward, Vector3.zero)));
            Assert.IsTrue(float.IsPositiveInfinity(GazeMath.AngleBetweenDeg(Vector3.forward, new Vector3(float.NaN, 0f, 1f))));
        }

        [Test]
        public void AngleDeg_measures_from_the_head_position_along_the_forward_ray()
        {
            Vector3 head = new Vector3(1f, 2f, 3f);
            Vector3 forward = Vector3.right;
            Assert.AreEqual(0f, GazeMath.AngleDeg(head, forward, new Vector3(6f, 2f, 3f)), 1e-3f);
            Assert.AreEqual(45f, GazeMath.AngleDeg(head, forward, new Vector3(6f, 7f, 3f)), 1e-3f);
            Assert.AreEqual(90f, GazeMath.AngleDeg(head, forward, new Vector3(1f, 2f, 9f)), 1e-3f);
            Assert.AreEqual(180f, GazeMath.AngleDeg(head, forward, new Vector3(-4f, 2f, 3f)), 1e-3f);
        }

        [Test]
        public void AngleDeg_is_infinity_when_the_target_is_on_the_head()
        {
            Vector3 head = new Vector3(1f, 2f, 3f);
            Assert.IsTrue(float.IsPositiveInfinity(GazeMath.AngleDeg(head, Vector3.forward, head)));
        }

        [Test]
        public void AngularRadiusToWorld_and_back_round_trip()
        {
            float r = GazeMath.AngularRadiusToWorld(2f, 3f);
            Assert.AreEqual(2f * Mathf.Tan(3f * Mathf.Deg2Rad), r, 1e-6f);
            Assert.AreEqual(0.1048f, r, 1e-4f);
            Assert.AreEqual(3f, GazeMath.WorldRadiusToAngularDeg(2f, r), 1e-3f);
        }

        // SelectNearest: the head is at the origin looking along +Z; targets sit 2 m away at a given yaw.

        static GazeMath.Candidate At(float yawDeg, float enterDeg = 3f, float exitDeg = 4f, bool armed = true, bool sticky = false)
        {
            float yaw = yawDeg * Mathf.Deg2Rad;
            Vector3 position = new Vector3(Mathf.Sin(yaw), 0f, Mathf.Cos(yaw)) * 2f;
            return new GazeMath.Candidate(position, enterDeg, exitDeg, armed, sticky);
        }

        static int Select(out float angle, params GazeMath.Candidate[] candidates)
        {
            return GazeMath.SelectNearest(Vector3.zero, Vector3.forward, candidates, candidates.Length, out angle);
        }

        [Test]
        public void SelectNearest_returns_minus_one_for_no_candidates_or_a_null_array()
        {
            float angle;
            Assert.AreEqual(-1, Select(out angle));
            Assert.IsTrue(float.IsPositiveInfinity(angle));
            Assert.AreEqual(-1, GazeMath.SelectNearest(Vector3.zero, Vector3.forward, null, 5, out angle));
        }

        [Test]
        public void SelectNearest_selects_a_target_inside_its_enter_angle_and_reports_the_angle()
        {
            float angle;
            Assert.AreEqual(0, Select(out angle, At(2f)));
            Assert.AreEqual(2f, angle, 1e-3f);
        }

        [Test]
        public void SelectNearest_ignores_a_target_outside_its_enter_angle()
        {
            float angle;
            Assert.AreEqual(-1, Select(out angle, At(3.5f)));
            Assert.IsTrue(float.IsPositiveInfinity(angle));
        }

        [Test]
        public void SelectNearest_picks_the_smallest_angle_among_several()
        {
            float angle;
            Assert.AreEqual(2, Select(out angle, At(2.5f), At(-2f), At(1f), At(30f)));
            Assert.AreEqual(1f, angle, 1e-3f);
        }

        [Test]
        public void SelectNearest_applies_each_targets_own_radius()
        {
            // The small target is nearer to the gaze (1.5 deg) but only 1 deg wide, so the big one (3 deg away, 5 deg wide) wins.
            float angle;
            Assert.AreEqual(1, Select(out angle, At(1.5f, 1f, 1.25f), At(3f, 5f, 6.25f)));
            Assert.AreEqual(3f, angle, 1e-3f);
        }

        [Test]
        public void SelectNearest_skips_candidates_that_are_not_armed()
        {
            float angle;
            Assert.AreEqual(1, Select(out angle, At(0.5f, armed: false), At(2f)));
            Assert.AreEqual(-1, Select(out angle, At(0.5f, armed: false)));
        }

        [Test]
        public void SelectNearest_hysteresis_a_target_in_the_exit_band_is_selectable_only_if_sticky()
        {
            float angle;
            Assert.AreEqual(-1, Select(out angle, At(3.5f, 3f, 4f, sticky: false)), "not dwelling yet: enter angle applies");
            Assert.AreEqual(0, Select(out angle, At(3.5f, 3f, 4f, sticky: true)), "already dwelling: exit angle applies");
            Assert.AreEqual(3.5f, angle, 1e-3f);
            Assert.AreEqual(-1, Select(out angle, At(4.5f, 3f, 4f, sticky: true)), "beyond the exit angle even when sticky");
        }

        [Test]
        public void SelectNearest_a_sticky_candidate_never_gets_a_smaller_radius_from_a_low_exit_angle()
        {
            float angle;
            Assert.AreEqual(0, Select(out angle, At(2.5f, 3f, 1f, sticky: true)));
        }

        [Test]
        public void SelectNearest_a_tie_goes_to_the_lower_index()
        {
            float angle;
            Assert.AreEqual(0, Select(out angle, At(2f), At(-2f)));
            Assert.AreEqual(0, Select(out angle, At(-2f), At(2f)));
        }

        [Test]
        public void SelectNearest_reads_only_count_entries()
        {
            GazeMath.Candidate[] all = { At(2f), At(1f), At(0.1f) };
            float angle;
            Assert.AreEqual(1, GazeMath.SelectNearest(Vector3.zero, Vector3.forward, all, 2, out angle));
            Assert.AreEqual(2, GazeMath.SelectNearest(Vector3.zero, Vector3.forward, all, 3, out angle));
            Assert.AreEqual(2, GazeMath.SelectNearest(Vector3.zero, Vector3.forward, all, 99, out angle), "count beyond the array is clamped");
            Assert.AreEqual(-1, GazeMath.SelectNearest(Vector3.zero, Vector3.forward, all, 0, out angle));
        }

        [Test]
        public void SelectNearest_ignores_targets_behind_the_head_and_targets_on_the_head()
        {
            float angle;
            GazeMath.Candidate behind = new GazeMath.Candidate(new Vector3(0f, 0f, -2f), 3f, 4f, true, false);
            GazeMath.Candidate onHead = new GazeMath.Candidate(Vector3.zero, 3f, 4f, true, false);
            Assert.AreEqual(-1, Select(out angle, behind, onHead));
        }

        [Test]
        public void SelectNearest_measures_from_the_head_position_and_orientation()
        {
            Vector3 head = new Vector3(10f, 1.5f, -4f);
            Vector3 forward = Vector3.right;   // looking along +X
            GazeMath.Candidate[] candidates =
            {
                new GazeMath.Candidate(head + new Vector3(3f, 0f, 0f), 3f, 4f, true, false),      // dead ahead
                new GazeMath.Candidate(head + new Vector3(0f, 0f, 3f), 3f, 4f, true, false),      // 90 deg to the side
            };
            float angle;
            Assert.AreEqual(0, GazeMath.SelectNearest(head, forward, candidates, 2, out angle));
            Assert.AreEqual(0f, angle, 1e-3f);
        }

        [Test]
        public void Looking_at_a_target_for_a_second_and_a_half_fires_it_once_then_the_cooldown_holds_it()
        {
            // The whole chain, as GazeDwell runs it: angle from GazeMath -> DwellTimer.Tick, at 60 fps, a 3-degree target
            // 2 m ahead and a head that stays 1 degree off its centre.
            Vector3 head = Vector3.zero;
            float yaw = 1f * Mathf.Deg2Rad;
            Vector3 forward = new Vector3(Mathf.Sin(yaw), 0f, Mathf.Cos(yaw));
            Vector3 target = new Vector3(0f, 0f, 2f);
            var timer = new DwellTimer(3f, 3.75f, 1.2f, 1f);

            const float frame = 1f / 60f;
            int fires = 0;
            float firedAt = -1f;
            for (int i = 0; i < 90; i++)    // 1.5 s
            {
                float angle = GazeMath.AngleDeg(head, forward, target);
                if (timer.Tick(angle, frame)) { fires++; firedAt = i * frame; }
            }
            Assert.AreEqual(1, fires);
            Assert.AreEqual(1.2f, firedAt, 2f * frame);
            Assert.AreEqual(DwellState.Cooldown, timer.State, "still locked out 0.3 s after firing");
        }

        // ------------------------------------------------------------------------------------------------ DwellEngine
        // One frame of gaze-dwell with fake targets: the head is at the origin; items sit 2 m away at a given yaw, so the
        // gaze at yaw g is |g - yaw| degrees from an item (for yaws on the same side).

        sealed class Item : IDwellItem
        {
            public readonly string Name;
            public Vector3 Position { get; set; }
            public DwellTimer Timer { get; private set; }

            public Item(string name, float yawDeg, float radiusDeg = 3f)
            {
                Name = name;
                Position = Direction(yawDeg) * 2f;
                Timer = new DwellTimer(radiusDeg, radiusDeg * 1.25f, 1f, 1f);
            }

            public override string ToString() { return Name; }
        }

        static Vector3 Direction(float yawDeg)
        {
            float yaw = yawDeg * Mathf.Deg2Rad;
            return new Vector3(Mathf.Sin(yaw), 0f, Mathf.Cos(yaw));
        }

        static void Step(DwellEngine engine, IDwellItem[] items, float gazeYawDeg, bool confirm = false, bool hasGaze = true)
        {
            engine.Step(Vector3.zero, Direction(gazeYawDeg), hasGaze, items, items.Length, Dt, confirm);
        }

        [Test]
        public void Engine_focuses_the_item_the_gaze_is_on_and_advances_only_its_timer()
        {
            var a = new Item("A", 0f);
            var b = new Item("B", 20f);
            var engine = new DwellEngine();
            var items = new IDwellItem[] { a, b };

            Step(engine, items, 1f);
            Assert.AreSame(a, engine.Focused);
            Assert.IsNull(engine.Previous);
            Assert.IsTrue(engine.FocusChanged);
            Assert.AreEqual(DwellState.Dwelling, a.Timer.State);
            Assert.AreEqual(DwellState.Idle, b.Timer.State);

            Step(engine, items, 1f);
            Assert.IsFalse(engine.FocusChanged, "same focus on the second frame");
            Assert.AreEqual(0.25f, a.Timer.Progress, 1e-6f);
            Assert.AreEqual(0f, b.Timer.Progress);
        }

        [Test]
        public void Engine_focuses_nothing_when_the_gaze_is_outside_every_enter_angle()
        {
            var engine = new DwellEngine();
            var items = new IDwellItem[] { new Item("A", 0f), new Item("B", 20f) };
            Step(engine, items, 10f);
            Assert.IsNull(engine.Focused);
            Assert.IsFalse(engine.FocusChanged);
            Assert.AreEqual(0, engine.FiredCount);
        }

        [Test]
        public void Engine_reports_a_completed_dwell_exactly_once_then_the_item_leaves_the_focus()
        {
            var a = new Item("A", 0f);
            var engine = new DwellEngine();
            var items = new IDwellItem[] { a };

            for (int i = 0; i < 4; i++)           // entering tick, then 0.25, 0.5, 0.75 s held
            {
                Step(engine, items, 0f);
                Assert.AreEqual(0, engine.FiredCount, "step " + i);
            }
            Step(engine, items, 0f);              // 1.0 s held
            Assert.AreEqual(1, engine.FiredCount);
            Assert.AreSame(a, engine.FiredAt(0));
            Assert.IsNull(engine.Forced, "a dwell, not the confirm key");
            Assert.AreSame(a, engine.Focused, "still focused on the frame it fires");

            Step(engine, items, 0f);              // in cooldown now: cannot be focused
            Assert.AreEqual(0, engine.FiredCount);
            Assert.IsNull(engine.Focused);
            Assert.IsTrue(engine.FocusChanged);
            Assert.AreSame(a, engine.Previous);
        }

        [Test]
        public void Engine_hands_the_focus_over_when_the_gaze_moves_and_resets_the_old_dwell()
        {
            var a = new Item("A", 0f);
            var b = new Item("B", 10f);
            var engine = new DwellEngine();
            var items = new IDwellItem[] { a, b };

            Step(engine, items, 0f); Step(engine, items, 0f); Step(engine, items, 0f);
            Assert.AreEqual(0.5f, a.Timer.Progress, 1e-6f);

            Step(engine, items, 10f);
            Assert.AreSame(b, engine.Focused);
            Assert.AreSame(a, engine.Previous);
            Assert.IsTrue(engine.FocusChanged);
            Assert.AreEqual(DwellState.Idle, a.Timer.State);
            Assert.AreEqual(0f, a.Timer.Progress, "A's progress is lost, not paused");
            Assert.AreEqual(DwellState.Dwelling, b.Timer.State);
        }

        [Test]
        public void Engine_hysteresis_keeps_a_focus_that_drifts_into_the_exit_band_but_never_acquires_there()
        {
            var a = new Item("A", 0f);                 // enter 3 deg, exit 3.75 deg
            var engine = new DwellEngine();
            var items = new IDwellItem[] { a };

            Step(engine, items, 3.5f);
            Assert.IsNull(engine.Focused, "a fresh approach inside the band is not enough to start");

            Step(engine, items, 2f);
            Assert.AreSame(a, engine.Focused);

            Step(engine, items, 3.5f);
            Assert.AreSame(a, engine.Focused, "already dwelling: the wider exit angle applies");
            Assert.IsFalse(engine.FocusChanged);
            Assert.AreEqual(0.25f, a.Timer.Progress, 1e-6f);

            Step(engine, items, 3.9f);
            Assert.IsNull(engine.Focused, "beyond the exit angle");
            Assert.AreEqual(DwellState.Idle, a.Timer.State);
        }

        [Test]
        public void Engine_skips_an_item_in_cooldown_so_an_overlapping_neighbour_gets_the_focus()
        {
            var a = new Item("A", 0f);
            var b = new Item("B", 2f);                // overlaps A: the gaze at 0.5 deg is 0.5 from A and 1.5 from B
            var engine = new DwellEngine();
            var items = new IDwellItem[] { a, b };

            for (int i = 0; i < 5; i++) Step(engine, items, 0.5f);
            Assert.AreEqual(1, engine.FiredCount);
            Assert.AreSame(a, engine.FiredAt(0));

            Step(engine, items, 0.5f);
            Assert.AreSame(b, engine.Focused, "A is locked out, so the next nearest item takes over");
            Assert.AreEqual(DwellState.Dwelling, b.Timer.State);
        }

        [Test]
        public void Engine_lets_unfocused_items_finish_their_cooldown()
        {
            var a = new Item("A", 0f);
            var engine = new DwellEngine();
            var items = new IDwellItem[] { a };

            for (int i = 0; i < 5; i++) Step(engine, items, 0f);          // fires on the 5th
            Assert.AreEqual(DwellState.Fired, a.Timer.State);
            for (int i = 0; i < 4; i++) Step(engine, items, 45f);         // looking elsewhere: Fired -> Cooldown -> ... -> Idle
            Assert.AreEqual(DwellState.Idle, a.Timer.State);
            Assert.IsTrue(a.Timer.IsArmed);
        }

        [Test]
        public void Engine_without_a_gaze_focuses_nothing_and_releases_a_running_dwell()
        {
            var a = new Item("A", 0f);
            var engine = new DwellEngine();
            var items = new IDwellItem[] { a };

            Step(engine, items, 0f); Step(engine, items, 0f);
            Assert.AreEqual(DwellState.Dwelling, a.Timer.State);

            Step(engine, items, 0f, hasGaze: false);
            Assert.IsNull(engine.Focused);
            Assert.AreEqual(DwellState.Idle, a.Timer.State);
        }

        [Test]
        public void Confirm_fires_the_focused_item_at_once_and_leaves_it_in_the_fired_state_for_that_frame()
        {
            var a = new Item("A", 0f);
            var engine = new DwellEngine();
            var items = new IDwellItem[] { a };

            Step(engine, items, 0f); Step(engine, items, 0f);             // 0.25 s held
            Step(engine, items, 0f, confirm: true);
            Assert.AreSame(a, engine.Forced);
            Assert.AreEqual(1, engine.FiredCount);
            Assert.AreSame(a, engine.FiredAt(0));
            Assert.IsFalse(engine.ConfirmedRaised, "a focused item took the press");
            Assert.AreEqual(DwellState.Fired, a.Timer.State, "observers see Fired for one frame");

            Step(engine, items, 0f);
            Assert.AreEqual(DwellState.Cooldown, a.Timer.State);
            Assert.AreEqual(0, engine.FiredCount);
        }

        [Test]
        public void Confirm_with_nothing_focused_is_a_global_confirmation_and_fires_nothing()
        {
            var a = new Item("A", 0f);
            var engine = new DwellEngine();
            var items = new IDwellItem[] { a };

            Step(engine, items, 30f, confirm: true);
            Assert.IsTrue(engine.ConfirmedRaised);
            Assert.AreEqual(0, engine.FiredCount);
            Assert.IsNull(engine.Forced);
            Assert.AreEqual(DwellState.Idle, a.Timer.State);

            Step(engine, items, 30f);
            Assert.IsFalse(engine.ConfirmedRaised, "reported for one Step only");
        }

        [Test]
        public void Confirm_works_with_no_gaze_and_with_no_items_at_all()
        {
            var engine = new DwellEngine();
            Step(engine, new IDwellItem[0], 0f, confirm: true, hasGaze: false);
            Assert.IsTrue(engine.ConfirmedRaised);

            engine.Step(Vector3.zero, Vector3.forward, true, null, 0, Dt, true);
            Assert.IsTrue(engine.ConfirmedRaised);
        }

        [Test]
        public void Confirm_cannot_fire_an_item_twice_and_a_press_during_its_cooldown_is_a_global_confirmation()
        {
            var a = new Item("A", 0f);
            var engine = new DwellEngine();
            var items = new IDwellItem[] { a };

            Step(engine, items, 0f);
            Step(engine, items, 0f, confirm: true);
            Assert.AreEqual(1, engine.FiredCount);

            Step(engine, items, 0f, confirm: true);                      // A is in cooldown: nothing focused
            Assert.AreEqual(0, engine.FiredCount, "must not fire A a second time");
            Assert.IsTrue(engine.ConfirmedRaised);
        }

        [Test]
        public void Confirm_on_the_frame_a_dwell_would_complete_fires_once_not_twice()
        {
            var a = new Item("A", 0f);
            var engine = new DwellEngine();
            var items = new IDwellItem[] { a };

            for (int i = 0; i < 4; i++) Step(engine, items, 0f);          // 0.75 s held: the next tick would fire it
            Step(engine, items, 0f, confirm: true);
            Assert.AreEqual(1, engine.FiredCount);
            Assert.AreSame(a, engine.Forced);
        }

        [Test]
        public void Confirm_goes_to_the_nearest_of_two_overlapping_items()
        {
            var a = new Item("A", 0f);
            var b = new Item("B", 3f);
            var engine = new DwellEngine();
            var items = new IDwellItem[] { a, b };

            Step(engine, items, 2f, confirm: true);                       // 2 deg from A, 1 deg from B
            Assert.AreSame(b, engine.Forced);
            Assert.AreEqual(DwellState.Idle, a.Timer.State);
        }

        [Test]
        public void Engine_handles_more_items_than_its_initial_capacity()
        {
            var items = new IDwellItem[40];
            for (int i = 0; i < items.Length; i++) items[i] = new Item("T" + i, i * 8f);
            var engine = new DwellEngine();

            Step(engine, items, 33 * 8f + 1f);
            Assert.AreSame(items[33], engine.Focused);
        }

        [Test]
        public void Engine_reads_only_count_items_and_tolerates_a_null_array_or_a_count_that_is_too_big()
        {
            var a = new Item("A", 0f);
            var b = new Item("B", 5f);
            var items = new IDwellItem[] { a, b };
            var engine = new DwellEngine();

            engine.Step(Vector3.zero, Direction(5f), true, items, 1, Dt, false);   // B is beyond the count
            Assert.IsNull(engine.Focused);
            engine.Step(Vector3.zero, Direction(5f), true, items, 99, Dt, false);  // clamped to 2
            Assert.AreSame(b, engine.Focused);
            engine.Step(Vector3.zero, Direction(5f), true, null, 3, Dt, false);
            Assert.IsNull(engine.Focused);
            engine.Step(Vector3.zero, Direction(5f), true, items, -1, Dt, false);
            Assert.IsNull(engine.Focused);
        }

        [Test]
        public void Engine_Reset_forgets_the_focus_and_the_last_results()
        {
            var a = new Item("A", 0f);
            var engine = new DwellEngine();
            var items = new IDwellItem[] { a };
            Step(engine, items, 0f, confirm: true);
            Assert.AreEqual(1, engine.FiredCount);

            engine.Reset();
            Assert.IsNull(engine.Focused);
            Assert.IsNull(engine.Previous);
            Assert.IsNull(engine.Forced);
            Assert.AreEqual(0, engine.FiredCount);
            Assert.IsFalse(engine.ConfirmedRaised);
        }

        // ------------------------------------------------------------------------------------------------ ReticleGeometry
        // Distance 1 m, ring centred on 0.75 deg and 0.12 deg thick, arc band 0.30 deg thick, dot 0.08 deg radius.

        static ReticleGeometry NewReticle(float distanceM = 1f)
        {
            var g = new ReticleGeometry();
            g.SetShape(distanceM, 0.75f, 0.12f, 0.30f, 0.08f);
            return g;
        }

        static float Tan(float deg) { return Mathf.Tan(deg * Mathf.Deg2Rad); }

        static float TriangleCrossZ(ReticleGeometry g, int tri)
        {
            Vector3 a = g.Vertices[g.Triangles[tri * 3]];
            Vector3 b = g.Vertices[g.Triangles[tri * 3 + 1]];
            Vector3 c = g.Vertices[g.Triangles[tri * 3 + 2]];
            return (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
        }

        static float ArcArea(ReticleGeometry g)
        {
            float sum = 0f;
            int first = ReticleGeometry.RingSegments * 2;                    // the arc's triangles follow the ring's
            for (int t = first; t < first + ReticleGeometry.ArcSegments * 2; t++) sum += Mathf.Abs(TriangleCrossZ(g, t)) * 0.5f;
            return sum;
        }

        [Test]
        public void Reticle_mesh_has_valid_indices_flat_finite_vertices_and_no_orphans()
        {
            ReticleGeometry g = NewReticle();
            Assert.AreEqual(ReticleGeometry.VertexCount, g.Vertices.Length);
            Assert.AreEqual(ReticleGeometry.VertexCount, g.Colors.Length);
            Assert.AreEqual(ReticleGeometry.TriangleCount * 3, g.Triangles.Length);

            var used = new bool[g.Vertices.Length];
            for (int t = 0; t < ReticleGeometry.TriangleCount; t++)
            {
                int a = g.Triangles[t * 3], b = g.Triangles[t * 3 + 1], c = g.Triangles[t * 3 + 2];
                foreach (int i in new[] { a, b, c })
                {
                    Assert.GreaterOrEqual(i, 0, "triangle " + t);
                    Assert.Less(i, g.Vertices.Length, "triangle " + t);
                    used[i] = true;
                }
                Assert.IsTrue(a != b && b != c && a != c, "triangle " + t + " repeats a vertex");
            }
            for (int i = 0; i < used.Length; i++) Assert.IsTrue(used[i], "vertex " + i + " is not used by any triangle");

            foreach (Vector3 v in g.Vertices)
            {
                Assert.IsFalse(float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsInfinity(v.x) || float.IsInfinity(v.y));
                Assert.AreEqual(0f, v.z);
            }
        }

        [Test]
        public void Reticle_ring_and_dot_are_closed_loops_that_do_not_borrow_the_arcs_vertices()
        {
            ReticleGeometry g = NewReticle();
            var corners = new int[g.Vertices.Length];     // how many triangle corners use each vertex
            for (int i = 0; i < g.Triangles.Length; i++) corners[g.Triangles[i]]++;

            // A closed two-triangles-per-quad strip shares every vertex between exactly 3 corners.
            for (int i = ReticleGeometry.RingFirstVertex; i < ReticleGeometry.ArcFirstVertex; i++)
            {
                Assert.AreEqual(3, corners[i], "ring vertex " + i);
            }

            // The ring's triangles (the first RingSegments * 2) only touch ring vertices.
            for (int i = 0; i < ReticleGeometry.RingSegments * 6; i++)
            {
                Assert.Less(g.Triangles[i], ReticleGeometry.ArcFirstVertex, "ring triangle corner " + i);
            }

            // A closed fan: the centre is in every triangle, each rim vertex in exactly two.
            Assert.AreEqual(ReticleGeometry.DotSegments, corners[ReticleGeometry.DotFirstVertex]);
            for (int i = ReticleGeometry.DotFirstVertex + 1; i < ReticleGeometry.VertexCount; i++)
            {
                Assert.AreEqual(2, corners[i], "dot rim vertex " + i);
            }
        }

        [Test]
        public void Reticle_triangles_wind_clockwise_as_seen_by_the_wearer_at_every_progress()
        {
            ReticleGeometry g = NewReticle();
            foreach (float progress in new[] { 0f, 0.1f, 0.37f, 0.5f, 0.99f, 1f })
            {
                g.SetProgress(progress);
                for (int t = 0; t < ReticleGeometry.TriangleCount; t++)
                {
                    Assert.LessOrEqual(TriangleCrossZ(g, t), 1e-12f, "triangle " + t + " at progress " + progress + " faces away");
                }
            }
        }

        [Test]
        public void Reticle_arc_starts_at_twelve_oclock_and_sweeps_clockwise()
        {
            ReticleGeometry g = NewReticle();
            g.SetProgress(0.5f);
            float arcInner = Tan(0.6f), arcOuter = Tan(0.9f);       // ring 0.75 +/- half of the 0.30 band

            Vector3 start = g.Vertices[ReticleGeometry.ArcFirstVertex];
            Assert.AreEqual(0f, start.x, 1e-7f);
            Assert.AreEqual(arcInner, start.y, 1e-6f);
            Vector3 startOuter = g.Vertices[ReticleGeometry.ArcFirstVertex + 1];
            Assert.AreEqual(arcOuter, startOuter.y, 1e-6f);

            // The very next step moves to the wearer's right (+X): clockwise.
            Vector3 next = g.Vertices[ReticleGeometry.ArcFirstVertex + 2];
            Assert.Greater(next.x, 0f);
            Assert.Greater(next.y, 0f);
        }

        [Test]
        public void Reticle_arc_ends_at_three_oclock_for_a_quarter_and_six_oclock_for_a_half()
        {
            ReticleGeometry g = NewReticle();
            float arcInner = Tan(0.6f);
            int last = ReticleGeometry.ArcFirstVertex + 2 * ReticleGeometry.ArcSegments;

            g.SetProgress(0.25f);
            Assert.AreEqual(arcInner, g.Vertices[last].x, 1e-6f);
            Assert.AreEqual(0f, g.Vertices[last].y, 1e-6f);

            g.SetProgress(0.5f);
            Assert.AreEqual(0f, g.Vertices[last].x, 1e-6f);
            Assert.AreEqual(-arcInner, g.Vertices[last].y, 1e-6f);
        }

        [Test]
        public void Reticle_arc_vertices_beyond_the_sweep_collapse_onto_its_end()
        {
            ReticleGeometry g = NewReticle();
            g.SetProgress(0.25f);
            int endStep = ReticleGeometry.ArcSegments / 4;
            Vector3 end = g.Vertices[ReticleGeometry.ArcFirstVertex + 2 * endStep];
            for (int k = endStep; k <= ReticleGeometry.ArcSegments; k++)
            {
                Vector3 v = g.Vertices[ReticleGeometry.ArcFirstVertex + 2 * k];
                Assert.AreEqual(end.x, v.x, 1e-7f, "step " + k);
                Assert.AreEqual(end.y, v.y, 1e-7f, "step " + k);
            }
        }

        [Test]
        public void Reticle_arc_area_is_zero_when_empty_and_grows_in_proportion_to_progress()
        {
            ReticleGeometry g = NewReticle();
            float rIn = Tan(0.6f), rOut = Tan(0.9f);
            float bandArea = Mathf.PI * (rOut * rOut - rIn * rIn);

            g.SetProgress(0f);
            Assert.Less(ArcArea(g), bandArea * 1e-4f, "an empty arc must draw nothing");

            g.SetProgress(1f);
            Assert.AreEqual(bandArea, ArcArea(g), bandArea * 0.01f, "a full arc is the whole band");

            foreach (float p in new[] { 0.25f, 0.5f, 0.75f })
            {
                g.SetProgress(p);
                Assert.AreEqual(bandArea * p, ArcArea(g), bandArea * 0.02f, "progress " + p);
            }
        }

        [Test]
        public void Reticle_SetProgress_reports_changes_and_clamps()
        {
            ReticleGeometry g = NewReticle();
            Assert.IsFalse(g.SetProgress(0f), "already empty");
            Assert.IsTrue(g.SetProgress(0.5f));
            Assert.IsFalse(g.SetProgress(0.5f), "same value: nothing to re-upload");
            Assert.IsTrue(g.SetProgress(7f));
            Assert.AreEqual(1f, g.Progress);
            Assert.IsTrue(g.SetProgress(float.NaN));
            Assert.AreEqual(0f, g.Progress);
            Assert.IsFalse(g.SetProgress(-3f));
            Assert.AreEqual(0f, g.Progress);
        }

        [Test]
        public void Reticle_size_follows_the_angle_and_scales_with_distance()
        {
            var g = new ReticleGeometry();
            g.SetShape(1f, 1.0f, 0.2f, 0.4f, 0.1f);
            Assert.AreEqual(Tan(1.1f), g.Vertices[ReticleGeometry.RingFirstVertex + 1].y, 1e-6f, "ring outer edge at 12 o'clock");
            Assert.AreEqual(Tan(0.9f), g.Vertices[ReticleGeometry.RingFirstVertex].y, 1e-6f, "ring inner edge");
            Assert.AreEqual(Tan(0.9f), g.RingInnerRadius, 1e-6f);
            Assert.AreEqual(Tan(0.1f), g.DotRadius, 1e-6f);
            Assert.AreEqual(Tan(1.2f), g.OuterRadius, 1e-6f, "the arc band is the widest part");

            g.SetShape(2f, 1.0f, 0.2f, 0.4f, 0.1f);
            Assert.AreEqual(2f * Tan(1.1f), g.Vertices[ReticleGeometry.RingFirstVertex + 1].y, 1e-6f, "twice as far: twice the radius, same angle");
        }

        [Test]
        public void Reticle_centre_stays_open_only_the_small_dot_is_inside_the_ring()
        {
            ReticleGeometry g = NewReticle();
            g.SetProgress(1f);
            Assert.Greater(g.RingInnerRadius, 5f * g.DotRadius, "the ring must leave a clear gap around the dot");
            for (int i = ReticleGeometry.RingFirstVertex; i < ReticleGeometry.DotFirstVertex; i++)
            {
                Assert.Greater(g.Vertices[i].magnitude, 4f * g.DotRadius, "ring/arc vertex " + i + " intrudes on the middle");
            }
        }

        [Test]
        public void Reticle_zero_sized_parts_degenerate_instead_of_breaking()
        {
            var g = new ReticleGeometry();
            g.SetShape(1f, 0.75f, 0.12f, 0.30f, 0f);   // no dot
            Assert.AreEqual(0f, g.DotRadius);
            foreach (Vector3 v in g.Vertices) Assert.IsFalse(float.IsNaN(v.x) || float.IsNaN(v.y));
            g.SetShape(1f, 0.75f, 0f, 0f, 0.08f);       // zero thickness
            foreach (Vector3 v in g.Vertices) Assert.IsFalse(float.IsNaN(v.x) || float.IsNaN(v.y));
        }

        [Test]
        public void Reticle_colours_the_ring_with_the_dot_and_the_arc_independently()
        {
            ReticleGeometry g = NewReticle();
            var ring = new Color32(10, 20, 30, 40);
            var arc = new Color32(200, 210, 220, 230);
            g.SetRingColor(ring);
            g.SetArcColor(arc);
            for (int i = 0; i < g.Colors.Length; i++)
            {
                bool isArc = i >= ReticleGeometry.ArcFirstVertex && i < ReticleGeometry.DotFirstVertex;
                Color32 expected = isArc ? arc : ring;
                Assert.AreEqual(expected.r, g.Colors[i].r, "vertex " + i);
                Assert.AreEqual(expected.a, g.Colors[i].a, "vertex " + i);
            }
            g.SetRingColor(new Color32(1, 2, 3, 4));
            Assert.AreEqual(arc.r, g.Colors[ReticleGeometry.ArcFirstVertex].r, "recolouring the ring leaves the arc alone");
        }
    }
}
