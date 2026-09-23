//! Piecewise-linear fan curves with asymmetric hysteresis (instant rise, delayed fall).

use crate::config::{CurveConfig, ZoneConfig};
use crate::sensors::SensorReading;
use std::collections::HashMap;

/// One input to a curve, after zones have been resolved.
#[derive(Debug, Clone, PartialEq)]
pub enum Member {
    /// An exact sensor id. If it is unavailable the curve's fail-safe becomes a floor.
    Named(String),
    /// Every sensor whose id starts with `prefix`, minus `except`: the ids other zones
    /// have claimed explicitly. Empty for a wildcard written directly on a curve.
    Wildcard { prefix: String, except: Vec<String> },
}

impl Member {
    fn matches(&self, id: &str) -> bool {
        match self {
            Member::Named(named) => named == id,
            Member::Wildcard { prefix, except } => id.starts_with(prefix.as_str()) && !except.iter().any(|e| e == id),
        }
    }
}

/// Maps one or more sensor ids (aggregated by max: the hottest input drives the fan) to a
/// duty percentage.
#[derive(Debug, Clone)]
pub struct FanCurve {
    pub fan_channel_id: String,
    pub members: Vec<Member>,
    /// (temperature, duty percent), ascending by temperature. Guaranteed by config validation.
    pub points: Vec<(f64, u8)>,
    /// Duty may rise immediately but only drops once the driving temperature has fallen
    /// this far below the point that produced the current duty. Prevents the classic
    /// hunting at a curve breakpoint.
    pub hysteresis_celsius: f64,
    /// Applied instead of the curve when every sensor is unavailable, and as a floor under
    /// it when only some explicitly named sensor is. Per curve rather than global: how
    /// "safe" the number has to be depends on what the fan is protecting.
    pub fail_safe_duty_percent: u8,
}

impl FanCurve {
    /// Expects a config that passed validation (duties 0-100, every zone id known).
    pub fn from_config(config: &CurveConfig, zones: &[ZoneConfig]) -> Self {
        let mut members: Vec<Member> = config.sensor_ids.iter().map(|id| member(id, &[])).collect();

        for zone in zones.iter().filter(|zone| config.zones.contains(&zone.id)) {
            // What other zones pin down explicitly is not this zone's, however broad its wildcards.
            let claimed_elsewhere: Vec<String> = zones
                .iter()
                .filter(|other| other.id != zone.id)
                .flat_map(|other| other.sensor_ids.iter())
                .filter(|id| !is_wildcard(id))
                .cloned()
                .collect();
            members.extend(zone.sensor_ids.iter().map(|id| member(id, &claimed_elsewhere)));
        }

        Self {
            fan_channel_id: config.fan_channel_id.clone(),
            members,
            points: config.points.iter().map(|(temperature, duty)| (*temperature, (*duty).clamp(0, 100) as u8)).collect(),
            hysteresis_celsius: config.hysteresis_celsius,
            fail_safe_duty_percent: config.fail_safe_duty_percent.clamp(0, 100) as u8,
        }
    }
}

fn is_wildcard(id: &str) -> bool {
    id.ends_with(":*")
}

fn member(id: &str, claimed_elsewhere: &[String]) -> Member {
    if is_wildcard(id) {
        Member::Wildcard { prefix: id[..id.len() - 1].to_owned(), except: claimed_elsewhere.to_vec() }
    } else {
        Member::Named(id.to_owned())
    }
}

/// Stateful evaluator. Lives for the whole daemon lifetime so hysteresis state persists
/// across polls. This (one temperature/duty pair per channel) is state the control
/// algorithm itself needs; it is not history.
#[derive(Default)]
pub struct CurveEngine {
    /// Per channel: the (temperature, duty) last applied.
    last_applied: HashMap<String, (f64, u8)>,
}

impl CurveEngine {
    /// Duty percent to apply for this poll. An unreadable input is never treated as "cold":
    ///  - if none of the curve's sensors produced a reading, returns the fail-safe duty;
    ///  - if a sensor the curve names explicitly (not via a wildcard) is unavailable while
    ///    others still read, the curve runs on what's left but the fail-safe duty becomes a
    ///    floor, since the missing sensor could be the hot one.
    ///
    /// Wildcard members are exempt from the second rule: drives come and go (hot-swap,
    /// standby) and the ones still present are a fair stand-in for the group.
    pub fn evaluate(&mut self, curve: &FanCurve, readings: &[SensorReading]) -> u8 {
        let (driving_temperature, named_sensor_unavailable) = select_driving_temperature(curve, readings);
        let Some(temperature) = driving_temperature else {
            return curve.fail_safe_duty_percent;
        };

        let duty = self.apply_hysteresis(curve, temperature);
        if named_sensor_unavailable { duty.max(curve.fail_safe_duty_percent) } else { duty }
    }

    fn apply_hysteresis(&mut self, curve: &FanCurve, temperature: f64) -> u8 {
        let target = interpolate(&curve.points, temperature);

        if let Some(&(last_temperature, last_duty)) = self.last_applied.get(&curve.fan_channel_id) {
            let falling = target < last_duty;
            if falling && temperature > last_temperature - curve.hysteresis_celsius {
                return last_duty;
            }
        }

        self.last_applied.insert(curve.fan_channel_id.clone(), (temperature, target));
        target
    }
}

fn select_driving_temperature(curve: &FanCurve, readings: &[SensorReading]) -> (Option<f64>, bool) {
    let matches = |reading: &SensorReading| curve.members.iter().any(|member| member.matches(&reading.id));

    let available: Vec<&SensorReading> = readings.iter().filter(|r| r.celsius_or_null.is_some() && matches(r)).collect();
    let named_sensor_unavailable = curve
        .members
        .iter()
        .filter_map(|member| match member {
            Member::Named(id) => Some(id),
            Member::Wildcard { .. } => None,
        })
        .any(|id| !available.iter().any(|r| r.id == *id));
    let hottest = available.iter().filter_map(|r| r.celsius_or_null).reduce(f64::max);

    (hottest, named_sensor_unavailable)
}

fn interpolate(points: &[(f64, u8)], temperature: f64) -> u8 {
    let (Some(first), Some(last)) = (points.first(), points.last()) else {
        return 100;
    };

    if temperature <= first.0 {
        return first.1;
    }

    for pair in points.windows(2) {
        let ((low_temperature, low_duty), (high_temperature, high_duty)) = (pair[0], pair[1]);
        if temperature <= high_temperature {
            let span = high_temperature - low_temperature;
            if span <= 0.0 {
                return high_duty;
            }

            let fraction = (temperature - low_temperature) / span;
            return (f64::from(low_duty) + fraction * (f64::from(high_duty) - f64::from(low_duty))).round() as u8;
        }
    }

    last.1
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::sensors::SensorCategory;

    fn curve() -> FanCurve {
        FanCurve {
            fan_channel_id: "cpu".to_owned(),
            members: vec![Member::Named("cpu".to_owned())],
            points: vec![(30.0, 20), (50.0, 50), (70.0, 100)],
            hysteresis_celsius: 3.0,
            fail_safe_duty_percent: 100,
        }
    }

    fn curve_on(sensor_ids: &[&str], fail_safe: u8) -> FanCurve {
        FanCurve { members: sensor_ids.iter().map(|id| member(id, &[])).collect(), fail_safe_duty_percent: fail_safe, ..curve() }
    }

    fn zone(id: &str, sensor_ids: &[&str]) -> ZoneConfig {
        ZoneConfig { id: id.to_owned(), sensor_ids: sensor_ids.iter().map(|s| (*s).to_owned()).collect() }
    }

    fn zoned_curve(zones: &[&str], sensor_ids: &[&str]) -> CurveConfig {
        CurveConfig {
            fan_channel_id: "cage".to_owned(),
            zones: zones.iter().map(|z| (*z).to_owned()).collect(),
            sensor_ids: sensor_ids.iter().map(|s| (*s).to_owned()).collect(),
            points: vec![(30.0, 20), (50.0, 50), (70.0, 100)],
            hysteresis_celsius: 3.0,
            fail_safe_duty_percent: 80,
        }
    }

    /// The case that motivated zones: two SSDs are drive sensors but sit in the main
    /// compartment, so the drive-bay fan must not react to them.
    fn layout() -> Vec<ZoneConfig> {
        vec![zone("drive-bay", &["drive:*"]), zone("main", &["cpu", "hba", "drive:ssd1", "drive:ssd2"])]
    }

    fn reading(id: &str, celsius: f64) -> SensorReading {
        SensorReading::new(id, SensorCategory::Cpu, id, Some(celsius), "path")
    }

    fn unavailable(id: &str) -> SensorReading {
        SensorReading::new(id, SensorCategory::Cpu, id, None, "path")
    }

    #[test]
    fn interpolates_linearly_between_points() {
        // Halfway between (30,20) and (50,50) -> 35
        assert_eq!(CurveEngine::default().evaluate(&curve(), &[reading("cpu", 40.0)]), 35);
    }

    #[test]
    fn clamps_below_lowest_and_above_highest_point() {
        assert_eq!(CurveEngine::default().evaluate(&curve(), &[reading("cpu", 10.0)]), 20);
        assert_eq!(CurveEngine::default().evaluate(&curve(), &[reading("cpu", 90.0)]), 100);
    }

    #[test]
    fn vertical_step_takes_the_higher_duty() {
        let step = FanCurve { points: vec![(30.0, 30), (50.0, 40), (50.0, 80)], ..curve() };

        assert_eq!(CurveEngine::default().evaluate(&step, &[reading("cpu", 50.0)]), 40);
        assert_eq!(CurveEngine::default().evaluate(&step, &[reading("cpu", 50.1)]), 80);
    }

    #[test]
    fn fails_safe_when_no_driving_sensor_is_available() {
        assert_eq!(CurveEngine::default().evaluate(&curve(), &[unavailable("cpu")]), 100);
        assert_eq!(CurveEngine::default().evaluate(&curve_on(&["cpu"], 80), &[unavailable("cpu")]), 80);
        assert_eq!(CurveEngine::default().evaluate(&curve_on(&["cpu"], 80), &[]), 80);
    }

    #[test]
    fn rises_immediately_but_holds_until_temperature_drops_past_hysteresis() {
        let curve = FanCurve { hysteresis_celsius: 5.0, ..curve() };
        let mut engine = CurveEngine::default();

        assert_eq!(engine.evaluate(&curve, &[reading("cpu", 40.0)]), 35); // rise, applied immediately
        assert_eq!(engine.evaluate(&curve, &[reading("cpu", 38.0)]), 35); // small drop, within hysteresis: held
        assert_eq!(engine.evaluate(&curve, &[reading("cpu", 34.0)]), 26); // dropped >= 5C from 40: follows curve
    }

    #[test]
    fn hysteresis_state_is_tracked_per_channel() {
        let other = FanCurve { fan_channel_id: "case".to_owned(), ..curve() };
        let mut engine = CurveEngine::default();

        engine.evaluate(&curve(), &[reading("cpu", 60.0)]);

        assert_eq!(engine.evaluate(&other, &[reading("cpu", 40.0)]), 35);
    }

    #[test]
    fn a_zone_wildcard_skips_sensors_another_zone_names_explicitly() {
        let curve = FanCurve::from_config(&zoned_curve(&["drive-bay"], &[]), &layout());
        let readings = [reading("drive:hdd1", 40.0), reading("drive:ssd1", 65.0), reading("drive:ssd2", 70.0), reading("cpu", 90.0)];

        // Hot SSDs and CPU are ignored; the 40C HDD drives the cage fan.
        assert_eq!(CurveEngine::default().evaluate(&curve, &readings), 35);
    }

    #[test]
    fn a_zone_naming_a_sensor_explicitly_does_get_it() {
        let curve = FanCurve::from_config(&zoned_curve(&["main"], &[]), &layout());
        let readings = [
            reading("drive:hdd1", 90.0),
            reading("drive:ssd1", 40.0),
            reading("drive:ssd2", 20.0),
            reading("cpu", 30.0),
            reading("hba", 30.0),
        ];

        assert_eq!(CurveEngine::default().evaluate(&curve, &readings), 35);
        // Members named explicitly by the zone are still "named": if one (here drive:ssd2,
        // missing from the readings entirely) is unavailable, the fail-safe floor applies
        // exactly as for a sensor written directly on the curve.
        assert_eq!(CurveEngine::default().evaluate(&curve, &[reading("drive:ssd1", 40.0), reading("cpu", 30.0), reading("hba", 30.0)]), 80);
    }

    #[test]
    fn a_curves_own_wildcard_is_raw_and_zones_combine_with_sensor_ids() {
        // lsi-cooling style: hba plus every drive regardless of zone claims.
        let raw = FanCurve::from_config(&zoned_curve(&[], &["hba", "drive:*"]), &layout());
        let readings = [reading("drive:ssd1", 50.0), reading("hba", 30.0)];
        assert_eq!(CurveEngine::default().evaluate(&raw, &readings), 50);

        let combined = FanCurve::from_config(&zoned_curve(&["drive-bay"], &["hba"]), &layout());
        let readings = [reading("drive:hdd1", 30.0), reading("drive:ssd1", 90.0), reading("hba", 50.0)];
        assert_eq!(CurveEngine::default().evaluate(&combined, &readings), 50);
    }

    #[test]
    fn without_zones_from_config_matches_the_old_behaviour() {
        let curve = FanCurve::from_config(&zoned_curve(&[], &["cpu", "drive:*"]), &[]);

        assert_eq!(curve.members, vec![Member::Named("cpu".to_owned()), Member::Wildcard { prefix: "drive:".to_owned(), except: vec![] }]);
    }

    #[test]
    fn aggregates_multiple_sensors_by_max() {
        let duty = CurveEngine::default().evaluate(&curve_on(&["drive:*"], 100), &[reading("drive:a", 30.0), reading("drive:b", 50.0)]);

        assert_eq!(duty, 50);
    }

    #[test]
    fn fail_safe_becomes_a_floor_when_only_some_named_sensors_are_unavailable() {
        let curve = curve_on(&["gpu", "hba"], 80);

        // hba alone says 35, but the unreadable gpu could be the hot one.
        assert_eq!(CurveEngine::default().evaluate(&curve, &[unavailable("gpu"), reading("hba", 40.0)]), 80);
        // A named sensor missing from the readings entirely counts as unavailable too.
        assert_eq!(CurveEngine::default().evaluate(&curve, &[reading("hba", 40.0)]), 80);
        // The floor never lowers a hotter curve result.
        assert_eq!(CurveEngine::default().evaluate(&curve, &[reading("hba", 90.0)]), 100);
    }

    #[test]
    fn unavailable_wildcard_member_does_not_trigger_the_fail_safe_floor() {
        let duty = CurveEngine::default().evaluate(&curve_on(&["drive:*"], 80), &[reading("drive:a", 40.0), unavailable("drive:b")]);

        assert_eq!(duty, 35);
    }
}
