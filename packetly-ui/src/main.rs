//! Packetly native UI — live network traffic monitor (egui + egui_plot).
//! v3.1: tabs, smooth filled graph with custom axis overlay, Traffic 4-column view,
//! Usage tab with quota form, Alerts tab, zoom 100-175 %, light/dark themes,
//! per-app icons, custom date range, single-instance guard.

// GUI subsystem: no console window in release builds (closing one killed the app).
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

use eframe::egui;
use egui_plot::{Line, Plot, PlotPoints};
use std::collections::{HashMap, HashSet};
use std::sync::{Arc, Mutex};
use std::time::Duration;

const API: &str = "http://127.0.0.1:8912";
const TOP_N: usize = 10;

#[derive(Clone, Copy, PartialEq)]
enum ThemeKind {
    Dark,
    Light,
    Elite,
}

impl ThemeKind {
    fn label(self) -> &'static str {
        match self {
            ThemeKind::Dark => "Dark (AMOLED)",
            ThemeKind::Light => "Light",
            ThemeKind::Elite => "Elite (navy)",
        }
    }
    fn all() -> [ThemeKind; 3] {
        [ThemeKind::Dark, ThemeKind::Light, ThemeKind::Elite]
    }
}

#[derive(Clone)]
struct Theme {
    nav_bg: egui::Color32,
    bg: egui::Color32,
    plot_bg: egui::Color32,
    card: egui::Color32,
    card2: egui::Color32,
    border: egui::Color32,
    text: egui::Color32,
    dim: egui::Color32,
    grid: egui::Color32,
    axis: egui::Color32,
    accent: egui::Color32,
    in_c: egui::Color32,
    out_c: egui::Color32,
    danger: egui::Color32,
}

impl Theme {
    fn light() -> Self {
        Self {
            nav_bg: egui::Color32::WHITE,
            bg: egui::Color32::WHITE,
            plot_bg: egui::Color32::from_rgb(222, 240, 234),
            card: egui::Color32::WHITE,
            card2: egui::Color32::from_rgb(244, 247, 246),
            border: egui::Color32::from_rgb(226, 232, 230),
            text: egui::Color32::from_rgb(31, 41, 55),
            dim: egui::Color32::from_rgb(120, 130, 140),
            grid: egui::Color32::from_rgba_unmultiplied(255, 255, 255, 110),
            axis: egui::Color32::from_rgb(110, 122, 132),
            accent: egui::Color32::from_rgb(59, 130, 246),
            in_c: egui::Color32::from_rgb(134, 90, 222),
            out_c: egui::Color32::from_rgb(127, 181, 240),
            danger: egui::Color32::from_rgb(220, 80, 70),
        }
    }
    fn dark() -> Self {
        Self {
            nav_bg: egui::Color32::from_rgb(10, 12, 11),
            bg: egui::Color32::from_rgb(8, 10, 9),
            plot_bg: egui::Color32::from_rgb(0, 0, 0),
            card: egui::Color32::from_rgb(15, 19, 17),
            card2: egui::Color32::from_rgb(21, 26, 23),
            border: egui::Color32::from_rgb(30, 38, 34),
            text: egui::Color32::from_rgb(220, 232, 226),
            dim: egui::Color32::from_rgb(125, 143, 134),
            grid: egui::Color32::from_rgba_unmultiplied(255, 255, 255, 16),
            axis: egui::Color32::from_rgb(120, 138, 130),
            accent: egui::Color32::from_rgb(96, 165, 250),
            in_c: egui::Color32::from_rgb(122, 84, 235),
            out_c: egui::Color32::from_rgb(132, 178, 242),
            danger: egui::Color32::from_rgb(230, 100, 90),
        }
    }
    fn elite() -> Self {
        Self {
            nav_bg: egui::Color32::from_rgb(12, 17, 30),
            bg: egui::Color32::from_rgb(15, 21, 36),
            plot_bg: egui::Color32::from_rgb(18, 25, 42),
            card: egui::Color32::from_rgb(25, 33, 52),
            card2: egui::Color32::from_rgb(30, 40, 62),
            border: egui::Color32::from_rgb(42, 53, 82),
            text: egui::Color32::from_rgb(204, 212, 228),
            dim: egui::Color32::from_rgb(118, 130, 158),
            grid: egui::Color32::from_rgba_unmultiplied(255, 255, 255, 5),
            axis: egui::Color32::from_rgb(124, 137, 173),
            accent: egui::Color32::from_rgb(132, 179, 60),
            in_c: egui::Color32::from_rgb(224, 80, 200),
            out_c: egui::Color32::from_rgb(110, 147, 232),
            danger: egui::Color32::from_rgb(235, 90, 90),
        }
    }
    fn alpha(c: egui::Color32, a: u8) -> egui::Color32 {
        egui::Color32::from_rgba_unmultiplied(c.r(), c.g(), c.b(), a)
    }
}

const PALETTE: [egui::Color32; 10] = [
    egui::Color32::from_rgb(240, 130, 165),
    egui::Color32::from_rgb(242, 207, 60),
    egui::Color32::from_rgb(240, 139, 44),
    egui::Color32::from_rgb(79, 195, 161),
    egui::Color32::from_rgb(91, 155, 213),
    egui::Color32::from_rgb(167, 139, 218),
    egui::Color32::from_rgb(230, 106, 92),
    egui::Color32::from_rgb(124, 195, 92),
    egui::Color32::from_rgb(96, 165, 250),
    egui::Color32::from_rgb(200, 160, 120),
];

fn series_color(idx: usize) -> egui::Color32 {
    PALETTE[idx % PALETTE.len()]
}

#[derive(Clone, Copy, PartialEq)]
enum Range {
    M5,
    H3,
    H24,
    D7,
    D30,
    Unlimited,
}

impl Range {
    fn all() -> [Range; 6] {
        [
            Range::M5,
            Range::H3,
            Range::H24,
            Range::D7,
            Range::D30,
            Range::Unlimited,
        ]
    }
    fn key(self) -> &'static str {
        match self {
            Range::M5 => "5m",
            Range::H3 => "3h",
            Range::H24 => "24h",
            Range::D7 => "7d",
            Range::D30 => "30d",
            Range::Unlimited => "unlimited",
        }
    }
    fn label(self) -> &'static str {
        match self {
            Range::M5 => "5 Minutes",
            Range::H3 => "3 Hours",
            Range::H24 => "24 Hours",
            Range::D7 => "7 Days",
            Range::D30 => "30 Days",
            Range::Unlimited => "All Time",
        }
    }
    fn refresh(self) -> Duration {
        match self {
            Range::M5 => Duration::from_millis(1000),
            Range::H3 => Duration::from_millis(5000),
            _ => Duration::from_millis(60000),
        }
    }
}

#[derive(Clone, Copy, PartialEq)]
enum Tab {
    Graph,
    Traffic,
    Usage,
    Alerts,
}

impl Tab {
    fn label(self) -> &'static str {
        match self {
            Tab::Graph => "Traffic Monitor",
            Tab::Traffic => "Traffic",
            Tab::Usage => "Usage",
            Tab::Alerts => "Alerts",
        }
    }
}

#[derive(Clone, Copy, PartialEq)]
enum UsagePeriod {
    Today,
    Week,
    Month,
    M5,
    H3,
    H24,
    D7,
    D30,
    All,
    Custom,
}

impl UsagePeriod {
    fn key(self) -> &'static str {
        match self {
            UsagePeriod::Today => "today",
            UsagePeriod::Week => "week",
            UsagePeriod::Month => "month",
            _ => "today",
        }
    }
    /// span of the window in seconds; None for calendar periods
    fn span(self) -> Option<i64> {
        match self {
            UsagePeriod::M5 => Some(300),
            UsagePeriod::H3 => Some(3 * 3600),
            UsagePeriod::H24 => Some(86400),
            UsagePeriod::D7 => Some(7 * 86400),
            UsagePeriod::D30 => Some(30 * 86400),
            UsagePeriod::All => Some(0),
            _ => None,
        }
    }
    fn label(self) -> &'static str {
        match self {
            UsagePeriod::Today => "Today",
            UsagePeriod::Week => "This week",
            UsagePeriod::Month => "This month",
            UsagePeriod::M5 => "5 Minutes",
            UsagePeriod::H3 => "3 Hours",
            UsagePeriod::H24 => "24 Hours",
            UsagePeriod::D7 => "7 Days",
            UsagePeriod::D30 => "30 Days",
            UsagePeriod::All => "All Time",
            UsagePeriod::Custom => "Custom range",
        }
    }
    fn all() -> [UsagePeriod; 10] {
        [
            UsagePeriod::Today,
            UsagePeriod::Week,
            UsagePeriod::Month,
            UsagePeriod::M5,
            UsagePeriod::H3,
            UsagePeriod::H24,
            UsagePeriod::D7,
            UsagePeriod::D30,
            UsagePeriod::All,
            UsagePeriod::Custom,
        ]
    }
}

#[derive(Clone, PartialEq)]
enum RangeSel {
    Preset(Range),
    Custom(i64, i64),
}

impl RangeSel {
    fn query(&self) -> String {
        match self {
            RangeSel::Preset(r) => format!("range={}", r.key()),
            RangeSel::Custom(f, t) => format!("range=custom&from={}&to={}", f, t),
        }
    }
    fn label(&self) -> String {
        match self {
            RangeSel::Preset(r) => r.label().to_string(),
            RangeSel::Custom(f, t) => {
                let (_, m0, d0, hh0, mm0, _) = civil(*f);
                let (_, m1, d1, hh1, mm1, _) = civil(*t);
                if m0 == m1 && d0 == d1 {
                    format!("Custom {:02}:{:02}-{:02}:{:02}", hh0, mm0, hh1, mm1)
                } else {
                    format!("Custom {:02}/{:02}-{:02}/{:02}", d0, m0, d1, m1)
                }
            }
        }
    }
    fn refresh(&self) -> Duration {
        match self {
            RangeSel::Preset(r) => r.refresh(),
            RangeSel::Custom(f, t) => {
                if t - f <= 3 * 3600 {
                    Duration::from_millis(5000)
                } else {
                    Duration::from_millis(60000)
                }
            }
        }
    }
    fn x_step(&self, span: f64) -> f64 {
        match self {
            RangeSel::Preset(r) => match r {
                // GlassWire-matching label intervals:
                // 5m->20s, 3h->10m, 24h->1h, 7d->12h, 30d->2d, all->8d
                Range::M5 => 20.0,
                Range::H3 => 600.0,
                Range::H24 => 3600.0,
                Range::D7 => 43200.0,
                Range::D30 => 172800.0,
                Range::Unlimited => 691200.0,
            },
            RangeSel::Custom(_, _) => {
                // ~15 labels like the reference
                let raw = span / 15.0;
                for s in [
                    10.0, 20.0, 30.0, 60.0, 120.0, 300.0, 600.0, 1200.0, 1800.0,
                    3600.0, 7200.0, 14400.0, 21600.0, 43200.0, 86400.0, 172800.0,
                    691200.0,
                ] {
                    if raw <= s {
                        return s;
                    }
                }
                nice_step(raw)
            }
        }
    }
}

#[derive(Clone, Copy, PartialEq)]
enum AxisMode {
    Time,
    Hm,
    DayTime,
    Date,
}

fn axis_mode_for(sel: &RangeSel, span: f64) -> AxisMode {
    match sel {
        RangeSel::Preset(r) => match r {
            Range::M5 => AxisMode::Time,
            Range::H3 | Range::H24 => AxisMode::Hm,
            Range::D7 => AxisMode::DayTime,
            Range::D30 | Range::Unlimited => AxisMode::Date,
        },
        RangeSel::Custom(_, _) => {
            let step = sel.x_step(span);
            if step < 60.0 {
                AxisMode::Time
            } else if step < 3600.0 {
                AxisMode::Hm
            } else if step < 86400.0 {
                AxisMode::DayTime
            } else {
                AxisMode::Date
            }
        }
    }
}

#[derive(Clone)]
struct Series {
    name: String,
    color: egui::Color32,
    points: Vec<[f64; 2]>,
}

#[derive(Clone, Default)]
struct TrafficData {
    from: f64,
    to: f64,
    tier: String,
    rows: Vec<(f64, i64, f64, f64)>,
    names: HashMap<i64, String>,
}

#[derive(Clone, Default)]
struct GroupData {
    from: f64,
    to: f64,
    rows: Vec<(f64, i64, f64, f64)>,
    names: HashMap<i64, String>,
}


#[derive(Clone, Copy, Default)]
struct LanWan {
    lan_in: f64,
    lan_out: f64,
    wan_in: f64,
    wan_out: f64,
}

#[derive(Clone)]
struct AppRow {
    id: i64,
    name: String,
    icon: Option<Vec<u8>>,
    in_bytes: f64,
    out_bytes: f64,
}

#[derive(Clone)]
struct HostRow {
    label: String,
    region: Option<String>,
    in_bytes: f64,
    out_bytes: f64,
}

#[derive(Clone)]
struct QuotaRow {
    app_id: Option<i64>,
    app_name: String,
    period: String,
    limit: f64,
    used: f64,
    ratio: f64,
}

#[derive(Clone)]
struct EventRow {
    id: i64,
    ts: f64,
    kind: i64,
    app_name: Option<String>,
    data: String,
}

#[derive(Clone, Copy, PartialEq)]
enum SortKey {
    Total,
    Down,
    Up,
    Name,
    Hosts,
}

impl SortKey {
    fn label(self) -> &'static str {
        match self {
            SortKey::Total => "Total",
            SortKey::Down => "Download",
            SortKey::Up => "Upload",
            SortKey::Name => "Name",
            SortKey::Hosts => "Hosts",
        }
    }
    fn all() -> [SortKey; 4] {
        [SortKey::Total, SortKey::Down, SortKey::Up, SortKey::Name]
    }

    fn detail_keys() -> [SortKey; 4] {
        [SortKey::Name, SortKey::Hosts, SortKey::Down, SortKey::Up]
    }
}

fn sort_rows(
    rows: &mut [(String, Option<String>, egui::Color32, Option<i64>, f64, f64)],
    key: SortKey,
) {
    match key {
        SortKey::Total => rows.sort_by(|a, b| {
            (b.4 + b.5)
                .partial_cmp(&(a.4 + a.5))
                .unwrap_or(std::cmp::Ordering::Equal)
        }),
        SortKey::Down => rows.sort_by(|a, b| {
            b.4.partial_cmp(&a.4)
                .unwrap_or(std::cmp::Ordering::Equal)
        }),
        SortKey::Up => rows.sort_by(|a, b| {
            b.5.partial_cmp(&a.5)
                .unwrap_or(std::cmp::Ordering::Equal)
        }),
        SortKey::Name => rows.sort_by(|a, b| a.0.to_lowercase().cmp(&b.0.to_lowercase())),
        SortKey::Hosts => rows.sort_by(|a, b| {
            (b.4 + b.5)
                .partial_cmp(&(a.4 + a.5))
                .unwrap_or(std::cmp::Ordering::Equal)
        }),
    }
}

#[derive(Clone, Default)]
struct SelApp {
    app_id: i64,
    name: String,
    host_count: i64,
    in_bytes: f64,
    out_bytes: f64,
}

#[derive(Clone, Default)]
struct AppHostRow {
    label: String,
    region: Option<String>,
    in_bytes: f64,
    out_bytes: f64,
}

#[derive(Clone, Default)]
struct AppDetail {
    app_id: i64,
    name: String,
    path: Option<String>,
    publisher: Option<String>,
    version: Option<String>,
    in_bytes: f64,
    out_bytes: f64,
    alert_count: i64,
    hosts: Vec<AppHostRow>,
    range: (i64, i64),
}

#[derive(Clone, Default)]
struct UiFlags {
    startup_state: Option<bool>,
    startup_pending: bool,
    geoip_state: Option<bool>,
}

#[derive(Clone, Default)]
struct SelData {
    apps: Vec<SelApp>,
    t0: f64,
    t1: f64,
    hosts: Vec<HostRow>,
}

#[derive(Clone, Default)]
struct Snapshot {
    traffic: Option<TrafficData>,
    apps: Vec<AppRow>,
    all_apps: Vec<(i64, String)>,
    hosts: Vec<HostRow>,
    type_data: Option<GroupData>,
    country_data: Option<GroupData>,
    icons: HashMap<i64, Vec<u8>>,
    lanwan: LanWan,
    usage_per_app: Vec<(i64, String, f64)>,
    usage_total: f64,
    quotas: Vec<QuotaRow>,
    events: Vec<EventRow>,
    unseen: i64,
    collector: String,
    status_ok: bool,
    error: Option<String>,
    sel: Option<SelData>,
    app_detail: Option<AppDetail>,
    usage_range: (i64, i64),
    app_version: String,
}

struct Rendered {
    ver: u64,
    r: usize,
    target_k: f64,
    series: Arc<Vec<Vec<[f64; 2]>>>,
}

struct ChartCache {
    version: u64,
    pivot: Option<Arc<Pivot>>,
    rendered: Option<Rendered>,
    mini: Option<(u64, Arc<Vec<Vec<[f64; 2]>>>)>,
}

#[derive(Clone, Default)]
struct Pivot {
    x0: f64,
    x1: f64,
    y_max: f64,
    bucket: f64,
    n: usize,
    series: Vec<Series>,
}

fn fmt_b(b: f64) -> String {
    let (d, s) = if b.abs() >= 1.0e9 {
        (b / 1.0e9, "GB")
    } else if b.abs() >= 1.0e6 {
        (b / 1.0e6, "MB")
    } else if b.abs() >= 1.0e3 {
        (b / 1.0e3, "KB")
    } else {
        (b, "B")
    };
    if s == "B" {
        format!("{} B", b as i64)
    } else {
        format!("{:.2} {}", d, s)
    }
}

fn fmt_rate(b: f64, seconds: f64) -> String {
    if seconds <= 0.0 {
        return "0 B/s".into();
    }
    fmt_b(b / seconds) + "/s"
}

fn hash_hue(s: &str) -> f32 {
    let mut h: u32 = 2166136261;
    for b in s.bytes() {
        h ^= b as u32;
        h = h.wrapping_mul(16777619);
    }
    ((h.wrapping_mul(137508) >> 8) % 360) as f32 / 10.0
}

fn color_for(key: &str) -> egui::Color32 {
    let (r, g, b) = hsv_to_rgb(hash_hue(key), 0.62, 0.62);
    egui::Color32::from_rgb(r, g, b)
}

fn hsv_to_rgb(h: f32, s: f32, v: f32) -> (u8, u8, u8) {
    let c = v * s;
    let x = c * (1.0 - ((h / 60.0) % 2.0 - 1.0).abs());
    let m = v - c;
    let (r1, g1, b1) = match h as i32 {
        0..=59 => (c, x, 0.0),
        60..=119 => (x, c, 0.0),
        120..=179 => (0.0, c, x),
        180..=239 => (0.0, x, c),
        240..=299 => (x, 0.0, c),
        _ => (c, 0.0, x),
    };
    (
        ((r1 + m) * 255.0) as u8,
        ((g1 + m) * 255.0) as u8,
        ((b1 + m) * 255.0) as u8,
    )
}

fn country_flag(code: &str) -> String {
    if code.len() == 2 {
        code.to_uppercase()
            .chars()
            .map(|c| char::from_u32(0x1F1E6 + c as u32 - 65).unwrap_or(' '))
            .collect()
    } else {
        String::new()
    }
}

fn days_from_civil(y: i64, m: i64, d: i64) -> i64 {
    let y = if m <= 2 { y - 1 } else { y };
    let era = y.div_euclid(400);
    let yoe = y - era * 400;
    let mp = (m + 9) % 12;
    let doy = (153 * mp + 2) / 3 + d - 1;
    let doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    era * 146097 + doe - 719468
}

fn civil(secs: i64) -> (i64, u32, u32, u32, u32, u32) {
    // convert to the machine's local zone — the old pure-UTC math showed times
    // offset by the local UTC offset (e.g. +03:30).
    use chrono::{Datelike, TimeZone, Timelike};
    let dt = chrono::Local
        .timestamp_opt(secs, 0)
        .single()
        .unwrap_or_else(|| chrono::Local.timestamp_opt(0, 0).single().unwrap_or_default());
    (
        dt.year() as i64,
        dt.month(),
        dt.day(),
        dt.hour(),
        dt.minute(),
        dt.second(),
    )
}

fn ymd_secs(y: i32, m: u32, d: u32, hh: u32, mi: u32, ss: u32) -> Option<i64> {
    use chrono::TimeZone;
    chrono::Local
        .with_ymd_and_hms(y, m, d, hh, mi, ss)
        .single()
        .map(|dt| dt.timestamp())
}

/// unix seconds of local midnight on the given day
fn local_midnight(secs: f64) -> f64 {
    let (y, m, d, _, _, _) = civil(secs as i64);
    ymd_secs(y as i32, m, d, 0, 0, 0).unwrap_or(secs as i64) as f64
}

/// x ticks anchored to the LOCAL wall clock so sub-day steps land on
/// :00/:20/:40, hours on the hour and day steps on local midnight
/// (epoch-multiple ticks were offset by the +03:30 timezone)
fn time_ticks(x0: f64, x1: f64, step: f64) -> Vec<f64> {
    if !(step > 0.0) || !(x1 > x0) {
        return Vec::new();
    }
    let anchor = local_midnight(x0);
    let first = anchor + ((x0 - anchor) / step).ceil() * step;
    let mut out = Vec::new();
    let mut ts = first;
    let mut guard = 0;
    while ts <= x1 && guard < 100_000 {
        out.push(ts);
        ts += step;
        guard += 1;
    }
    out
}

fn fmt_time_axis(secs: f64, mode: AxisMode) -> String {
    let (y, m, d, hh, mm, ss) = civil(secs as i64);
    match mode {
        AxisMode::Time => format!("{:02}:{:02}:{:02}", hh, mm, ss),
        AxisMode::Hm => format!("{:02}:{:02}", hh, mm),
        AxisMode::DayTime => format!("{:02}-{:02} {:02}:{:02}", m, d, hh, mm),
        AxisMode::Date => format!("{:04}-{:02}-{:02}", y, m, d),
    }
}

fn fmt_time_tooltip(secs: f64) -> String {
    let (y, m, d, hh, mm, ss) = civil(secs as i64);
    format!("{:04}-{:02}-{:02} {:02}:{:02}:{:02}", y, m, d, hh, mm, ss)
}

const MONTH_NAMES: [&str; 12] = [
    "JANUARY",
    "FEBRUARY",
    "MARCH",
    "APRIL",
    "MAY",
    "JUNE",
    "JULY",
    "AUGUST",
    "SEPTEMBER",
    "OCTOBER",
    "NOVEMBER",
    "DECEMBER",
];

fn days_in_month(y: i32, m: u32) -> u32 {
    use chrono::Datelike;
    let (ny, nm) = if m == 12 { (y + 1, 1) } else { (y, m + 1) };
    let first_next = chrono::NaiveDate::from_ymd_opt(ny, nm, 1).unwrap_or_default();
    let first_this = chrono::NaiveDate::from_ymd_opt(y, m, 1).unwrap_or_default();
    (first_next - first_this).num_days() as u32
}

/// GlassWire-style month calendar: click a day to view it, click a second
/// day to view the range between the two
fn calendar_body(ui: &mut egui::Ui, app: &mut PacketlyApp) {
    use chrono::Datelike;
    let t = app.theme();
    let now = chrono::Local::now();
    let today = (now.year(), now.month(), now.day());

    ui.horizontal(|ui| {
        if ui
            .add(
                egui::Button::new(egui::RichText::new("‹").size(14.0))
                    .fill(egui::Color32::TRANSPARENT),
            )
            .on_hover_text("Previous month")
            .clicked()
        {
            let (y, m) = app.cal_view;
            app.cal_view = if m == 1 { (y - 1, 12) } else { (y, m - 1) };
        }
        if ui
            .add(
                egui::Button::new(egui::RichText::new("›").size(14.0))
                    .fill(egui::Color32::TRANSPARENT),
            )
            .on_hover_text("Next month")
            .clicked()
        {
            let (y, m) = app.cal_view;
            app.cal_view = if m == 12 { (y + 1, 1) } else { (y, m + 1) };
        }
        ui.add_space(6.0);
        ui.strong(
            egui::RichText::new(format!(
                "{} {}",
                MONTH_NAMES[(app.cal_view.1 - 1) as usize],
                app.cal_view.0
            ))
            .size(12.5),
        );
    });
    ui.add_space(2.0);

    // range to highlight: mid-selection shows the anchor, otherwise the
    // currently applied custom range
    let range = match (app.cal_anchor, &app.sel) {
        (Some(a), _) => Some((a, a)),
        (None, RangeSel::Custom(f, t2)) => Some((*f, *t2)),
        _ => None,
    };

    let (vy, vm) = app.cal_view;
    let first_wd = chrono::NaiveDate::from_ymd_opt(vy, vm, 1)
        .map(|d| d.weekday().num_days_from_sunday())
        .unwrap_or(0);
    let dim = days_in_month(vy, vm);

    egui::Grid::new("cal-grid")
        .spacing([2.0, 2.0])
        .min_col_width(34.0)
        .show(ui, |ui| {
            for wd in ["Su", "Mo", "Tu", "We", "Th", "Fr", "Sa"] {
                ui.add_sized(
                    [34.0, 16.0],
                    egui::Label::new(egui::RichText::new(wd).size(10.5).color(t.dim)),
                );
            }
            ui.end_row();
            for _ in 0..first_wd {
                ui.add_sized([34.0, 22.0], egui::Label::new(egui::RichText::new("")));
            }
            let mut cell = first_wd;
            for d in 1..=dim {
                let day0 = ymd_secs(vy, vm, d, 0, 0, 0).unwrap_or(0);
                let day1 = day0 + 86399;
                let (endpoint, in_range) = match range {
                    Some((f, t2)) => {
                        let e0 = t2 - 86399;
                        (day0 == f || day0 == e0, day0 > f && day0 < e0)
                    }
                    None => (false, false),
                };
                let is_today = (vy, vm, d) == today;
                let fill = if endpoint {
                    Some(t.accent)
                } else if in_range {
                    Some(t.card2)
                } else {
                    None
                };
                let txt = if endpoint {
                    egui::Color32::from_rgb(15, 21, 36)
                } else if is_today {
                    t.in_c
                } else {
                    t.text
                };
                let mut btn =
                    egui::Button::new(egui::RichText::new(d.to_string()).size(11.5).color(txt));
                if let Some(f) = fill {
                    btn = btn.fill(f);
                }
                if ui.add_sized([34.0, 22.0], btn).clicked() {
                    match app.cal_anchor {
                        None => {
                            app.cal_anchor = Some(day0);
                            app.sel = RangeSel::Custom(day0, day1);
                        }
                        Some(a) if day0 > a => {
                            app.sel = RangeSel::Custom(a, day1);
                            app.cal_anchor = None;
                        }
                        Some(_) => {
                            app.cal_anchor = Some(day0);
                            app.sel = RangeSel::Custom(day0, day1);
                        }
                    }
                    let _ = app.tx.send(UiMsg::Refresh(
                        app.tab,
                        app.sel.clone(),
                        app.period,
                    ));
                }
                cell += 1;
                if cell % 7 == 0 {
                    ui.end_row();
                }
            }
        });
    ui.add_space(2.0);
    ui.weak(
        egui::RichText::new("Pick a day, then a second day for a range")
            .size(10.0)
            .color(t.dim),
    );
}

fn rel_time(ts: f64) -> String {
    let now = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_secs_f64())
        .unwrap_or(0.0);
    let s = (now - ts).max(0.0);
    if s < 60.0 {
        format!("{}s ago", s as i64)
    } else if s < 3600.0 {
        format!("{}m ago", (s / 60.0) as i64)
    } else if s < 86400.0 {
        format!("{}h ago", (s / 3600.0) as i64)
    } else {
        format!("{}d ago", (s / 86400.0) as i64)
    }
}

fn nice_step(v: f64) -> f64 {
    if !(v > 0.0) {
        return 1.0;
    }
    let e = 10f64.powf(v.log10().floor());
    let m = v / e;
    let s = if m <= 1.0 {
        1.0
    } else if m <= 2.0 {
        2.0
    } else if m <= 2.5 {
        2.5
    } else if m <= 5.0 {
        5.0
    } else {
        10.0
    };
    s * e
}

fn nice_ceil(v: f64) -> f64 {
    if !(v > 0.0) {
        return 1.0;
    }
    let e = 10f64.powf(v.log10().floor());
    let m = v / e;
    let s = if m <= 1.0 {
        1.0
    } else if m <= 2.0 {
        2.0
    } else if m <= 2.5 {
        2.5
    } else if m <= 5.0 {
        5.0
    } else {
        10.0
    };
    s * e
}

fn moving_avg(pts: &[[f64; 2]], radius: usize) -> Vec<[f64; 2]> {
    if radius == 0 || pts.is_empty() {
        return pts.to_vec();
    }
    let n = pts.len();
    let mut pre = vec![0.0_f64; n + 1];
    for i in 0..n {
        pre[i + 1] = pre[i] + pts[i][1];
    }
    let mut out = Vec::with_capacity(n);
    for i in 0..n {
        let a = i.saturating_sub(radius);
        let b = (i + radius).min(n - 1);
        let v = (pre[b + 1] - pre[a]) / ((b - a + 1) as f64);
        out.push([pts[i][0], v]);
    }
    out
}

fn smooth(pts: &[[f64; 2]], max_out: usize) -> Vec<[f64; 2]> {
    if pts.len() < 3 {
        return pts.to_vec();
    }
    // Bound control points on high-count spans (24h/7d) — already Gaussian-smoothed
    // well above Nyquist — so Catmull-Rom stays crisp and fast on every range.
    let sampled_buf: Vec<[f64; 2]>;
    let ctrl: &[[f64; 2]] = if pts.len() > 600 {
        let target_ctrl = 480usize;
        let last = pts.len() - 1;
        sampled_buf = (0..target_ctrl)
            .map(|i| {
                let idx = (i * last + (target_ctrl - 1) / 2) / (target_ctrl - 1);
                pts[idx]
            })
            .collect();
        &sampled_buf
    } else {
        pts
    };
    let bins = ((max_out as f64) / (ctrl.len() as f64)).ceil().clamp(1.0, 12.0) as usize;
    let mut out = Vec::with_capacity(ctrl.len() * bins + 1);
    for i in 0..ctrl.len() - 1 {
        let p0 = ctrl[i.saturating_sub(1)];
        let p1 = ctrl[i];
        let p2 = ctrl[i + 1];
        let p3 = ctrl[(i + 2).min(ctrl.len() - 1)];
        for s in 0..bins {
            let t = s as f64 / bins as f64;
            let t2 = t * t;
            let t3 = t2 * t;
            let x = 0.5
                * ((2.0 * p1[0])
                    + (-p0[0] + p2[0]) * t
                    + (2.0 * p0[0] - 5.0 * p1[0] + 4.0 * p2[0] - p3[0]) * t2
                    + (-p0[0] + 3.0 * p1[0] - 3.0 * p2[0] + p3[0]) * t3);
            let y = (0.5
                * ((2.0 * p1[1])
                    + (-p0[1] + p2[1]) * t
                    + (2.0 * p0[1] - 5.0 * p1[1] + 4.0 * p2[1] - p3[1]) * t2
                    + (-p0[1] + 3.0 * p1[1] - 3.0 * p2[1] + p3[1]) * t3))
                .max(0.0);
            out.push([x, y]);
        }
    }
    out.push(*ctrl.last().unwrap());
    out
}

/// Clip a sorted curve to [vx0, vx1] with exact linear interpolation at the
/// viewport edges, applying peak-normalization scale `k` and manual `cap`.
fn clip_and_scale(pts: &[[f64; 2]], vx0: f64, vx1: f64, k: f64, cap: f64) -> Vec<[f64; 2]> {
    if pts.is_empty() || vx1 <= vx0 {
        return Vec::new();
    }
    let n = pts.len();
    let mut out = Vec::with_capacity(n.min(1600));
    let start = pts.partition_point(|p| p[0] < vx0);
    if start > 0 && start < n {
        let a = pts[start - 1];
        let b = pts[start];
        let dx = b[0] - a[0];
        let t = if dx > 0.0 {
            ((vx0 - a[0]) / dx).clamp(0.0, 1.0)
        } else {
            0.0
        };
        let y = a[1] + (b[1] - a[1]) * t;
        out.push([vx0, (y * k).min(cap).max(0.0)]);
    } else if start == 0 && pts[0][0] > vx0 {
        out.push([vx0, (pts[0][1] * k).min(cap).max(0.0)]);
    }
    let mut end = start;
    while end < n && pts[end][0] <= vx1 {
        out.push([pts[end][0], (pts[end][1] * k).min(cap).max(0.0)]);
        end += 1;
    }
    if end > 0 && end < n {
        let a = pts[end - 1];
        let b = pts[end];
        let dx = b[0] - a[0];
        let t = if dx > 0.0 {
            ((vx1 - a[0]) / dx).clamp(0.0, 1.0)
        } else {
            0.0
        };
        let y = a[1] + (b[1] - a[1]) * t;
        out.push([vx1, (y * k).min(cap).max(0.0)]);
    } else if end == n && pts[n - 1][0] < vx1 {
        out.push([vx1, (pts[n - 1][1] * k).min(cap).max(0.0)]);
    }
    out
}

fn traffic_ver(tr: &TrafficData) -> u64 {
    let last_sum = tr
        .rows
        .last()
        .map(|r| (r.2 + r.3).to_bits())
        .unwrap_or(0);
    (tr.rows.len() as u64)
        .wrapping_mul(31)
        .wrapping_add(tr.to as u64)
        .wrapping_mul(31)
        .wrapping_add(tr.from as u64)
        .wrapping_mul(31)
        .wrapping_add(last_sum)
}

fn fetch_json(url: &str) -> Result<serde_json::Value, String> {
    let resp = ureq::get(url)
        .timeout(Duration::from_secs(5))
        .call()
        .map_err(|e| e.to_string())?;
    resp.into_json::<serde_json::Value>().map_err(|e| e.to_string())
}

fn parse_group(d: &serde_json::Value) -> GroupData {
    let mut names = HashMap::new();
    if let Some(m) = d["data"]["keyNames"].as_object() {
        for (k, v) in m {
            if let Ok(id) = k.parse::<i64>() {
                names.insert(id, v.as_str().unwrap_or("?").to_string());
            }
        }
    }
    let mut rows = Vec::new();
    if let Some(arr) = d["data"]["rows"].as_array() {
        for r in arr {
            if r.as_array().map_or(false, |a| a.len() == 4) {
                rows.push((
                    r[0].as_f64().unwrap_or(0.0),
                    r[1].as_i64().unwrap_or(0),
                    r[2].as_f64().unwrap_or(0.0),
                    r[3].as_f64().unwrap_or(0.0),
                ));
            }
        }
    }
    GroupData {
        from: d["from"].as_f64().unwrap_or(0.0),
        to: d["to"].as_f64().unwrap_or(0.0),
        rows,
        names,
    }
}

fn fetch_traffic(q: &str) -> Result<TrafficData, String> {
    // Request aggregate per-bucket totals (10x-30x smaller & faster than breakdown=apps)
    let t = fetch_json(&format!("{}/api/traffic?{}", API, q))?;
    let mut rows = Vec::new();
    if let Some(arr) = t["data"]["totals"].as_array() {
        rows.reserve(arr.len());
        for r in arr {
            if r.as_array().map_or(false, |a| a.len() >= 3) {
                rows.push((
                    r[0].as_f64().unwrap_or(0.0),
                    0_i64,
                    r[1].as_f64().unwrap_or(0.0),
                    r[2].as_f64().unwrap_or(0.0),
                ));
            }
        }
    } else if let Some(arr) = t["data"]["rows"].as_array() {
        rows.reserve(arr.len());
        for r in arr {
            if r.as_array().map_or(false, |a| a.len() == 4) {
                rows.push((
                    r[0].as_f64().unwrap_or(0.0),
                    r[1].as_i64().unwrap_or(0),
                    r[2].as_f64().unwrap_or(0.0),
                    r[3].as_f64().unwrap_or(0.0),
                ));
            }
        }
    }
    Ok(TrafficData {
        from: t["from"].as_f64().unwrap_or(0.0),
        to: t["to"].as_f64().unwrap_or(0.0),
        tier: t["tier"].as_str().unwrap_or("").to_string(),
        rows,
        names: HashMap::new(),
    })
}

fn parse_appinfo(d: &serde_json::Value, app_id: i64, f: i64, t: i64) -> AppDetail {
    AppDetail {
        app_id,
        name: d["name"].as_str().unwrap_or("?").to_string(),
        path: d["path"].as_str().map(|x| x.to_string()),
        publisher: d["publisher"].as_str().map(|x| x.to_string()),
        version: d["version"].as_str().map(|x| x.to_string()),
        in_bytes: d["inBytes"].as_f64().unwrap_or(0.0),
        out_bytes: d["outBytes"].as_f64().unwrap_or(0.0),
        alert_count: d["alertCount"].as_i64().unwrap_or(0),
        hosts: d["hosts"]
            .as_array()
            .map(|arr| {
                arr.iter()
                    .map(|h| AppHostRow {
                        label: h["hostname"]
                            .as_str()
                            .map(|x| x.to_string())
                            .unwrap_or_else(|| h["ip"].as_str().unwrap_or("?").to_string()),
                        region: h["region"].as_str().map(|x| x.to_string()),
                        in_bytes: h["inBytes"].as_f64().unwrap_or(0.0),
                        out_bytes: h["outBytes"].as_f64().unwrap_or(0.0),
                    })
                    .collect()
            })
            .unwrap_or_default(),
        range: (f, t),
    }
}

fn query_startup() -> bool {
    std::process::Command::new("schtasks")
        .args(["/query", "/tn", "Packetly"])
        .output()
        .map(|o| o.status.success())
        .unwrap_or(false)
}

/// Enable/disable the logon task. Spawns an elevated process (UAC prompt)
/// because a highest-privilege task can only be registered elevated.
fn set_startup(enable: bool) -> bool {
    let exe = std::env::current_exe()
        .ok()
        .and_then(|p| p.parent().map(|d| d.join("packetly.exe")))
        .filter(|p| p.exists())
        .unwrap_or_else(|| {
            std::path::Path::new(
                &std::env::var("LOCALAPPDATA").unwrap_or_else(|_| ".".to_string()),
            )
            .join("Packetly")
            .join("packetly.exe")
        });
    if !exe.exists() {
        return false;
    }
    let arg = if enable { "--install-autostart" } else { "--uninstall-autostart" };
    std::process::Command::new("powershell")
        .args([
            "-NoProfile",
            "-Command",
            &format!(
                "Start-Process -FilePath '{}' -ArgumentList '{}' -Verb RunAs -Wait | Out-Null",
                exe.display(),
                arg
            ),
        ])
        .status()
        .map(|st| st.success())
        .unwrap_or(false)
}

fn fetch_geoip_state() -> Option<bool> {
    fetch_json(&format!("{}/api/settings", API))
        .ok()
        .map(|d| d["geoip"].as_bool().unwrap_or(false))
}

fn set_geoip_state(enable: bool) -> Option<bool> {
    let body = serde_json::json!({ "geoip": enable });
    ureq::post(&format!("{}/api/settings", API))
        .timeout(Duration::from_secs(4))
        .send_json(body)
        .ok()?;
    fetch_geoip_state()
}

fn fetch_snapshot(
    tab: &Tab,
    sel: &RangeSel,
    period: UsagePeriod,
    prev: Option<&Snapshot>,
    tick: u64,
) -> Snapshot {
    let mut snap = Snapshot::default();
    let q = sel.query();
    let full_meta = !matches!(tab, Tab::Graph) || tick == 0 || tick % 5 == 0 || prev.is_none();

    match fetch_traffic(&q) {
        Ok(t) => snap.traffic = Some(t),
        Err(e) => {
            snap.error = Some(e);
            return snap;
        }
    }

    // Apps & hosts lists are only displayed on the Traffic tab — skip them on
    // Traffic Monitor so live 1-second polling stays sub-5ms.
    if matches!(tab, Tab::Traffic) {
        if let Ok(d) = fetch_json(&format!("{}/api/apps?{}&limit=10", API, q)) {
            if let Some(arr) = d["apps"].as_array() {
                snap.apps = arr
                    .iter()
                    .map(|a| AppRow {
                        id: a["id"].as_i64().unwrap_or(0),
                        name: a["name"].as_str().unwrap_or("?").to_string(),
                        icon: None,
                        in_bytes: a["inBytes"].as_f64().unwrap_or(0.0),
                        out_bytes: a["outBytes"].as_f64().unwrap_or(0.0),
                    })
                    .collect();
            }
        }
        if let Ok(d) = fetch_json(&format!("{}/api/hosts?{}&limit=12", API, q)) {
            if let Some(arr) = d["hosts"].as_array() {
                snap.hosts = arr
                    .iter()
                    .map(|h| HostRow {
                        label: h["hostname"]
                            .as_str()
                            .map(|s| s.to_string())
                            .unwrap_or_else(|| h["ip"].as_str().unwrap_or("?").to_string()),
                        region: h["region"].as_str().map(|s| s.to_string()),
                        in_bytes: h["inBytes"].as_f64().unwrap_or(0.0),
                        out_bytes: h["outBytes"].as_f64().unwrap_or(0.0),
                    })
                    .collect();
            }
        }
    } else if let Some(p) = prev {
        snap.apps = p.apps.clone();
        snap.hosts = p.hosts.clone();
    }

    if let Ok(d) = fetch_json(&format!("{}/api/traffic?{}&group=lan", API, q)) {
        snap.lanwan = LanWan {
            lan_in: d["lanIn"].as_f64().unwrap_or(0.0),
            lan_out: d["lanOut"].as_f64().unwrap_or(0.0),
            wan_in: d["wanIn"].as_f64().unwrap_or(0.0),
            wan_out: d["wanOut"].as_f64().unwrap_or(0.0),
        };
    } else if let Some(p) = prev {
        snap.lanwan = p.lanwan;
    }

    if full_meta {
        if let Ok(d) = fetch_json(&format!("{}/api/status", API)) {
            snap.collector = d["collector"]
                .as_str()
                .unwrap_or("")
                .split(" (")
                .next()
                .unwrap_or("")
                .to_string();
            snap.status_ok = true;
            snap.app_version = d["version"].as_str().unwrap_or("?").to_string();
        }
    } else if let Some(p) = prev {
        snap.collector = p.collector.clone();
        snap.status_ok = p.status_ok;
        snap.app_version = p.app_version.clone();
    }

    match tab {
        Tab::Graph => {
            if full_meta {
                if let Ok(d) = fetch_json(&format!("{}/api/events?limit=50", API)) {
                    snap.unseen = d["unseen"].as_i64().unwrap_or(0);
                    if let Some(arr) = d["events"].as_array() {
                        snap.events = arr
                            .iter()
                            .map(|e| EventRow {
                                id: e["id"].as_i64().unwrap_or(0),
                                ts: e["ts"].as_f64().unwrap_or(0.0),
                                kind: e["type"].as_i64().unwrap_or(0),
                                app_name: e["appName"].as_str().map(|s| s.to_string()),
                                data: e["data"].as_str().unwrap_or("").to_string(),
                            })
                            .collect();
                    }
                }
            } else if let Some(p) = prev {
                snap.events = p.events.clone();
                snap.unseen = p.unseen;
            }
        }
        Tab::Traffic => {
            if let Ok(d) = fetch_json(&format!("{}/api/traffic?{}&group=type", API, q)) {
                snap.type_data = Some(parse_group(&d));
            }
            if let Ok(d) = fetch_json(&format!("{}/api/traffic?{}&group=country", API, q)) {
                snap.country_data = Some(parse_group(&d));
            }
        }
        Tab::Usage => {
            let usage_q = if let Some(span) = period.span() {
                let now = std::time::SystemTime::now()
                    .duration_since(std::time::UNIX_EPOCH)
                    .map(|d| d.as_secs() as i64)
                    .unwrap_or(0);
                let f = if span == 0 { 0 } else { now - span };
                format!("from={}&to={}", f, now)
            } else if period == UsagePeriod::Custom {
                match sel {
                    RangeSel::Custom(f, t) => format!("from={}&to={}", f, t),
                    _ => "period=today".to_string(),
                }
            } else {
                format!("period={}", period.key())
            };
            if let Ok(d) = fetch_json(&format!("{}/api/usage?{}", API, usage_q)) {
                snap.usage_total = d["total"].as_f64().unwrap_or(0.0);
                snap.usage_range = (
                    d["start"].as_i64().unwrap_or(0),
                    d["now"].as_i64().unwrap_or(0),
                );
                if let Some(arr) = d["perApp"].as_array() {
                    snap.usage_per_app = arr
                        .iter()
                        .take(15)
                        .map(|a| {
                            (
                                a["appId"].as_i64().unwrap_or(0),
                                a["name"].as_str().unwrap_or("?").to_string(),
                                a["bytes"].as_f64().unwrap_or(0.0),
                            )
                        })
                        .collect();
                }
            }
            if let Ok(d) = fetch_json(&format!("{}/api/quotas", API)) {
                if let Some(arr) = d["quotas"].as_array() {
                    snap.quotas = arr
                        .iter()
                        .map(|q| QuotaRow {
                            app_id: q["appId"].as_i64(),
                            app_name: q["appName"].as_str().unwrap_or("All apps").to_string(),
                            period: q["period"].as_str().unwrap_or("daily").to_string(),
                            limit: q["limitBytes"].as_f64().unwrap_or(1.0),
                            used: q["usedBytes"].as_f64().unwrap_or(0.0),
                            ratio: q["ratio"].as_f64().unwrap_or(0.0),
                        })
                        .collect();
                }
            }
            if let Ok(d) = fetch_json(&format!("{}/api/apps?range=30d&limit=500", API)) {
                if let Some(arr) = d["apps"].as_array() {
                    snap.all_apps = arr
                        .iter()
                        .map(|a| {
                            (
                                a["id"].as_i64().unwrap_or(0),
                                a["name"].as_str().unwrap_or("?").to_string(),
                            )
                        })
                        .collect();
                }
            }
        }
        Tab::Alerts => {
            if let Ok(d) = fetch_json(&format!("{}/api/events?limit=100", API)) {
                snap.unseen = d["unseen"].as_i64().unwrap_or(0);
                if let Some(arr) = d["events"].as_array() {
                    snap.events = arr
                        .iter()
                        .map(|e| EventRow {
                            id: e["id"].as_i64().unwrap_or(0),
                            ts: e["ts"].as_f64().unwrap_or(0.0),
                            kind: e["type"].as_i64().unwrap_or(0),
                            app_name: e["appName"].as_str().map(|s| s.to_string()),
                            data: e["data"].as_str().unwrap_or("").to_string(),
                        })
                        .collect();
                }
            }
        }
    }
    snap
}

fn tier_bucket(tier: &str) -> f64 {
    match tier {
        "traffic_60s" => 60.0,
        "traffic_3600s" => 3600.0,
        "traffic_86400s" => 86400.0,
        _ => 1.0,
    }
}

fn pivot(traffic: &TrafficData, in_c: egui::Color32, out_c: egui::Color32) -> Pivot {
    let mut p = Pivot::default();
    let bucket = tier_bucket(&traffic.tier);
    p.bucket = bucket;
    let first = traffic.rows.first().map(|r| r.0).unwrap_or(traffic.from);
    let mut x0 = ((traffic.from.max(first - bucket)) / bucket).ceil() * bucket;
    if traffic.to <= x0 {
        x0 = (traffic.from / bucket).ceil() * bucket;
    }
    let n = (((traffic.to - x0) / bucket).floor() as usize + 1).min(25000);
    if n == 0 {
        return p;
    }
    p.x0 = x0;
    p.x1 = x0 + (n - 1) as f64 * bucket;
    p.n = n;

    let mut down = vec![0.0_f64; n];
    let mut up = vec![0.0_f64; n];
    for (ts, _app, i, o) in &traffic.rows {
        let idx = ((ts - x0) / bucket).round() as usize;
        if idx < n {
            down[idx] += i;
            up[idx] += o;
        }
    }

    // raw bucket peak -> basis of the y-scale
    let mut y_max = 0.0_f64;
    for v in down.iter().chain(up.iter()) {
        y_max = y_max.max(*v);
    }
    p.y_max = y_max;
    p.series = vec![
        Series {
            name: "Upload".to_string(),
            color: out_c,
            points: (0..n).map(|i| [x0 + i as f64 * bucket, up[i]]).collect(),
        },
        Series {
            name: "Download".to_string(),
            color: in_c,
            points: (0..n)
                .map(|i| [x0 + i as f64 * bucket, down[i]])
                .collect(),
        },
    ];
    p
}

// ---- procedural country flags (painted shapes only: zero RAM/storage cost) ----

/// True when we can draw a small flag for this ISO-3166 alpha-2 code.
fn flag_known(code: &str) -> bool {
    matches!(
        code,
        "DE" | "FR" | "IT" | "NL" | "BE" | "RU" | "IE" | "AT" | "BG" | "HU" | "RO" | "PL"
            | "UA" | "EE" | "LT" | "LV" | "LU" | "US" | "GB" | "CN" | "JP" | "TR" | "IR"
            | "IN" | "ES" | "PT" | "SE" | "NO" | "FI" | "DK" | "GR" | "CA"
    )
}

fn hstripe(p: &egui::Painter, rect: &egui::Rect, frac: f32, y0: f32, color: egui::Color32) {
    let h = (rect.height() * frac).ceil().max(1.0);
    p.rect_filled(
        egui::Rect::from_min_size(
            egui::Pos2::new(rect.left(), rect.top() + rect.height() * y0),
            egui::vec2(rect.width(), h),
        ),
        0.0,
        color,
    );
}

fn vstripe(p: &egui::Painter, rect: &egui::Rect, frac: f32, x0: f32, color: egui::Color32) {
    let w = (rect.width() * frac).ceil().max(1.0);
    p.rect_filled(
        egui::Rect::from_min_size(
            egui::Pos2::new(rect.left() + rect.width() * x0, rect.top()),
            egui::vec2(w, rect.height()),
        ),
        0.0,
        color,
    );
}

fn disc(p: &egui::Painter, c: egui::Pos2, r: f32, color: egui::Color32) {
    p.circle_filled(c, r, color);
}

/// Draw a small simplified flag for `code` inside `rect`.
fn draw_flag(p: &egui::Painter, rect: &egui::Rect, code: &str) {
    use egui::Color32;
    let red = Color32::from_rgb(206, 60, 58);
    let white = Color32::from_rgb(240, 240, 240);
    let blue = Color32::from_rgb(46, 84, 165);
    let dark = Color32::from_rgb(28, 28, 30);
    let yellow = Color32::from_rgb(240, 200, 60);
    let green = Color32::from_rgb(52, 140, 90);
    let cx = egui::Pos2::new(rect.center().x, rect.center().y);
    match code {
        "DE" => {
            hstripe(p, rect, 1.0, 0.0, dark);
            hstripe(p, rect, 1.0 / 3.0, 1.0 / 3.0, red);
            hstripe(p, rect, 1.0 / 3.0, 2.0 / 3.0, yellow);
        }
        "FR" => {
            p.rect_filled(*rect, 0.0, blue);
            vstripe(p, rect, 1.0 / 3.0, 1.0 / 3.0, white);
            vstripe(p, rect, 1.0 / 3.0, 2.0 / 3.0, red);
        }
        "IT" => {
            p.rect_filled(*rect, 0.0, green);
            vstripe(p, rect, 1.0 / 3.0, 1.0 / 3.0, white);
            vstripe(p, rect, 1.0 / 3.0, 2.0 / 3.0, red);
        }
        "NL" => {
            hstripe(p, rect, 1.0, 0.0, red);
            hstripe(p, rect, 1.0 / 3.0, 1.0 / 3.0, white);
            hstripe(p, rect, 1.0 / 3.0, 2.0 / 3.0, blue);
        }
        "RU" => {
            p.rect_filled(*rect, 0.0, white);
            hstripe(p, rect, 1.0 / 3.0, 1.0 / 3.0, blue);
            hstripe(p, rect, 1.0 / 3.0, 2.0 / 3.0, red);
        }
        "IE" | "CI" => {
            p.rect_filled(*rect, 0.0, green);
            vstripe(p, rect, 1.0 / 3.0, 1.0 / 3.0, white);
            vstripe(p, rect, 1.0 / 3.0, 2.0 / 3.0, Color32::from_rgb(240, 140, 40));
        }
        "AT" => {
            p.rect_filled(*rect, 0.0, red);
            hstripe(p, rect, 1.0 / 3.0, 1.0 / 3.0, white);
            hstripe(p, rect, 1.0 / 3.0, 2.0 / 3.0, red);
        }
        "BG" => {
            p.rect_filled(*rect, 0.0, white);
            hstripe(p, rect, 2.0 / 3.0, 1.0 / 3.0, green);
            hstripe(p, rect, 1.0 / 3.0, 2.0 / 3.0, red);
        }
        "HU" => {
            p.rect_filled(*rect, 0.0, red);
            hstripe(p, rect, 1.0 / 3.0, 1.0 / 3.0, white);
            hstripe(p, rect, 1.0 / 3.0, 2.0 / 3.0, green);
        }
        "RO" => {
            p.rect_filled(*rect, 0.0, blue);
            vstripe(p, rect, 1.0 / 3.0, 1.0 / 3.0, yellow);
            vstripe(p, rect, 1.0 / 3.0, 2.0 / 3.0, red);
        }
        "PL" => {
            p.rect_filled(*rect, 0.0, white);
            hstripe(p, rect, 0.5, 0.5, red);
        }
        "UA" => {
            p.rect_filled(*rect, 0.0, blue);
            hstripe(p, rect, 0.5, 0.5, yellow);
        }
        "EE" | "LT" | "LV" | "LU" => {
            // generic three-stripe stand-ins
            p.rect_filled(*rect, 0.0, blue);
            hstripe(p, rect, 1.0 / 3.0, 1.0 / 3.0, dark);
            hstripe(p, rect, 1.0 / 3.0, 2.0 / 3.0, white);
        }
        "US" => {
            p.rect_filled(*rect, 0.0, white);
            let sh = rect.height() / 7.0;
            let mut i = 0;
            while i < 7 {
                p.rect_filled(
                    egui::Rect::from_min_size(
                        egui::Pos2::new(rect.left(), rect.top() + i as f32 * sh),
                        egui::vec2(rect.width(), sh.ceil().max(1.0)),
                    ),
                    0.0,
                    red,
                );
                i += 2;
            }
            let cw = rect.width() * 0.42;
            let ch = rect.height() * 4.0 / 7.0;
            p.rect_filled(
                egui::Rect::from_min_size(rect.left_top(), egui::vec2(cw, ch)),
                0.0,
                blue,
            );
        }
        "GB" => {
            p.rect_filled(*rect, 0.0, blue);
            p.line_segment(
                [rect.left_top(), rect.right_bottom()],
                egui::Stroke::new(rect.height() * 0.18, white),
            );
            p.line_segment(
                [rect.right_top(), rect.left_bottom()],
                egui::Stroke::new(rect.height() * 0.18, white),
            );
            p.line_segment(
                [egui::Pos2::new(rect.center().x, rect.top()), egui::Pos2::new(rect.center().x, rect.bottom())],
                egui::Stroke::new(rect.height() * 0.28, white),
            );
            p.line_segment(
                [egui::Pos2::new(rect.left(), rect.center().y), egui::Pos2::new(rect.right(), rect.center().y)],
                egui::Stroke::new(rect.height() * 0.28, white),
            );
            p.line_segment(
                [egui::Pos2::new(rect.center().x, rect.top()), egui::Pos2::new(rect.center().x, rect.bottom())],
                egui::Stroke::new(rect.height() * 0.12, red),
            );
            p.line_segment(
                [egui::Pos2::new(rect.left(), rect.center().y), egui::Pos2::new(rect.right(), rect.center().y)],
                egui::Stroke::new(rect.height() * 0.12, red),
            );
        }
        "CN" => {
            p.rect_filled(*rect, 0.0, Color32::from_rgb(222, 41, 38));
            disc(p, egui::Pos2::new(rect.left() + rect.width() * 0.2, rect.top() + rect.height() * 0.3), rect.height() * 0.14, yellow);
        }
        "JP" => {
            p.rect_filled(*rect, 0.0, white);
            disc(p, cx, rect.height() * 0.3, Color32::from_rgb(188, 62, 74));
        }
        "TR" => {
            p.rect_filled(*rect, 0.0, red);
            disc(p, egui::Pos2::new(rect.center().x - rect.width() * 0.06, cx.y), rect.height() * 0.26, white);
            disc(p, egui::Pos2::new(rect.center().x - rect.width() * 0.02, cx.y), rect.height() * 0.2, red);
            disc(p, egui::Pos2::new(rect.center().x + rect.width() * 0.18, cx.y), rect.height() * 0.08, white);
        }
        "IR" => {
            p.rect_filled(*rect, 0.0, green);
            hstripe(p, rect, 1.0 / 3.0, 1.0 / 3.0, white);
            hstripe(p, rect, 1.0 / 3.0, 2.0 / 3.0, red);
        }
        "IN" => {
            p.rect_filled(*rect, 0.0, Color32::from_rgb(240, 140, 40));
            hstripe(p, rect, 1.0 / 3.0, 1.0 / 3.0, white);
            hstripe(p, rect, 1.0 / 3.0, 2.0 / 3.0, green);
            disc(p, cx, rect.height() * 0.14, Color32::from_rgb(30, 60, 140));
        }
        "ES" => {
            p.rect_filled(*rect, 0.0, red);
            hstripe(p, rect, 0.5, 0.25, yellow);
        }
        "PT" => {
            p.rect_filled(*rect, 0.0, red);
            vstripe(p, rect, 0.4, 0.0, green);
            disc(p, egui::Pos2::new(rect.left() + rect.width() * 0.4, cx.y), rect.height() * 0.2, yellow);
        }
        "SE" => {
            p.rect_filled(*rect, 0.0, Color32::from_rgb(40, 90, 160));
            p.line_segment(
                [egui::Pos2::new(rect.left() + rect.width() * 0.36, rect.top()), egui::Pos2::new(rect.left() + rect.width() * 0.36, rect.bottom())],
                egui::Stroke::new(rect.height() * 0.2, yellow),
            );
            p.line_segment(
                [egui::Pos2::new(rect.left(), rect.center().y), egui::Pos2::new(rect.right(), rect.center().y)],
                egui::Stroke::new(rect.height() * 0.2, yellow),
            );
        }
        "NO" => {
            p.rect_filled(*rect, 0.0, white);
            p.line_segment(
                [egui::Pos2::new(rect.left() + rect.width() * 0.36, rect.top()), egui::Pos2::new(rect.left() + rect.width() * 0.36, rect.bottom())],
                egui::Stroke::new(rect.height() * 0.26, blue),
            );
            p.line_segment(
                [egui::Pos2::new(rect.left(), rect.center().y), egui::Pos2::new(rect.right(), rect.center().y)],
                egui::Stroke::new(rect.height() * 0.26, blue),
            );
        }
        "FI" => {
            p.rect_filled(*rect, 0.0, white);
            p.line_segment(
                [egui::Pos2::new(rect.left() + rect.width() * 0.36, rect.top()), egui::Pos2::new(rect.left() + rect.width() * 0.36, rect.bottom())],
                egui::Stroke::new(rect.height() * 0.28, blue),
            );
            p.line_segment(
                [egui::Pos2::new(rect.left(), rect.center().y), egui::Pos2::new(rect.right(), rect.center().y)],
                egui::Stroke::new(rect.height() * 0.28, blue),
            );
        }
        "DK" => {
            p.rect_filled(*rect, 0.0, red);
            p.line_segment(
                [egui::Pos2::new(rect.left() + rect.width() * 0.36, rect.top()), egui::Pos2::new(rect.left() + rect.width() * 0.36, rect.bottom())],
                egui::Stroke::new(rect.height() * 0.24, white),
            );
            p.line_segment(
                [egui::Pos2::new(rect.left(), rect.center().y), egui::Pos2::new(rect.right(), rect.center().y)],
                egui::Stroke::new(rect.height() * 0.24, white),
            );
        }
        "GR" => {
            p.rect_filled(*rect, 0.0, white);
            let sh = rect.height() / 5.0;
            let mut i = 0;
            while i < 5 {
                p.rect_filled(
                    egui::Rect::from_min_size(
                        egui::Pos2::new(rect.left(), rect.top() + i as f32 * sh),
                        egui::vec2(rect.width(), (sh / 2.0).ceil().max(1.0)),
                    ),
                    0.0,
                    blue,
                );
                i += 2;
            }
            p.rect_filled(
                egui::Rect::from_min_size(rect.left_top(), egui::vec2(rect.width() * 0.36, rect.height() * 0.6)),
                0.0,
                blue,
            );
        }
        "CA" => {
            p.rect_filled(*rect, 0.0, white);
            vstripe(p, rect, 0.25, 0.0, red);
            vstripe(p, rect, 0.25, 0.75, red);
            let m = egui::Pos2::new(rect.center().x, rect.center().y);
            p.line_segment(
                [egui::Pos2::new(m.x, m.y - rect.height() * 0.22), egui::Pos2::new(m.x, m.y + rect.height() * 0.22)],
                egui::Stroke::new(rect.width() * 0.06, red),
            );
        }
        _ => {}
    }
}

/// Allocate a 16x11 flag rect (only when the code is supported) and draw it.
/// Returns false when nothing was drawn.
fn flag_ui(ui: &mut egui::Ui, code: &str) -> bool {
    if !flag_known(code) {
        return false;
    }
    let (rect, _) = ui.allocate_exact_size(egui::vec2(16.0, 11.0), egui::Sense::hover());
    draw_flag(ui.painter(), &rect, code);
    true
}

fn group_series(g: &GroupData) -> Vec<Series> {
    let first = g.rows.first().map(|r| r.0).unwrap_or(g.from);
    let mut x0 = ((g.from.max(first - 60.0)) / 60.0).ceil() * 60.0;
    if g.to <= x0 {
        x0 = (g.from / 60.0).ceil() * 60.0;
    }
    let n = (((g.to - x0) / 60.0).floor() as usize + 1).min(25000);
    if n == 0 {
        return Vec::new();
    }
    let mut totals: HashMap<i64, f64> = HashMap::new();
    for (_, key, i, o) in &g.rows {
        *totals.entry(*key).or_default() += i + o;
    }
    let mut order: Vec<(i64, f64)> = totals.into_iter().collect();
    order.sort_by(|a, b| b.1.partial_cmp(&a.1).unwrap_or(std::cmp::Ordering::Equal));
    let mut per_key: HashMap<i64, Vec<f64>> = HashMap::new();
    for (ts, key, i, o) in &g.rows {
        let idx = ((ts - x0) / 60.0).round() as usize;
        if idx < n {
            let v = per_key.entry(*key).or_insert_with(|| vec![0.0; n]);
            v[idx] += i + o;
        }
    }
    let mut series = Vec::new();
    for (si, (key, _)) in order.iter().take(TOP_N).enumerate() {
        let name = g.names.get(key).cloned().unwrap_or_else(|| format!("?{}", key));
        let empty = vec![0.0; n];
        let vals = per_key.get(key).unwrap_or(&empty);
        series.push(Series {
            name,
            color: series_color(si),
            points: (0..n).map(|i| [x0 + i as f64 * 60.0, vals[i]]).collect(),
        });
    }
    series
}

struct PacketlyApp {
    tab: Tab,
    sel: RangeSel,
    cal_view: (i32, u32),
    cal_anchor: Option<i64>,
    show_custom: bool,
    period: UsagePeriod,
    zoom: f32,
    theme_kind: ThemeKind,
    snap: Arc<Mutex<Arc<Snapshot>>>,
    tx: std::sync::mpsc::Sender<UiMsg>,
    cache: Mutex<ChartCache>,
    icon_tex: HashMap<i64, egui::TextureHandle>,
    quota_app: String,
    quota_period: String,
    quota_value: String,
    quota_unit: i64,
    auto_scale: bool,
    scale_cap: f64,
    show_scale: bool,
    sel_drag: Option<(f64, f64)>,
    traffic_sort: SortKey,
    sel_sort: SortKey,
    detail_tab: usize,
    detail_sort: SortKey,
    settings_open: bool,
    start_minimized: bool,
    flags: Arc<Mutex<UiFlags>>,
    // Smooth 60fps viewport scroll & scale animation state
    anim_x1: f64,
    anim_y_top: f64,
    anim_k: f64,
    anim_last: Option<std::time::Instant>,
    anim_sel_key: String,
    snap_x1_seen: f64,
    snap_x1_at: Option<std::time::Instant>,
}

#[derive(Clone)]
enum UiMsg {
    Refresh(Tab, RangeSel, UsagePeriod),
    Selection(Option<(f64, f64)>),
    AppDetail(Option<(i64, i64, i64)>),
}

impl PacketlyApp {
    fn theme(&self) -> Theme {
        match self.theme_kind {
            ThemeKind::Dark => Theme::dark(),
            ThemeKind::Light => Theme::light(),
            ThemeKind::Elite => Theme::elite(),
        }
    }

    fn apply_visuals(ctx: &egui::Context, t: &Theme) {
        let dark = t.text.r() > 128;
        let mut vis = if dark {
            egui::Visuals::dark()
        } else {
            egui::Visuals::light()
        };
        vis.dark_mode = dark;
        vis.panel_fill = t.bg;
        vis.window_fill = t.card;
        vis.extreme_bg_color = t.card2;
        vis.override_text_color = Some(t.text);
        vis.widgets.noninteractive.fg_stroke = egui::Stroke::new(1.0, t.text);
        vis.widgets.noninteractive.bg_stroke = egui::Stroke::new(1.0, t.border);
        vis.widgets.noninteractive.bg_fill = t.card2;
        vis.widgets.inactive.fg_stroke = egui::Stroke::new(1.0, t.text);
        vis.widgets.inactive.bg_fill = t.card;
        vis.widgets.inactive.weak_bg_fill = t.card;
        vis.widgets.inactive.bg_stroke = egui::Stroke::new(1.0, t.border);
        vis.widgets.hovered.fg_stroke = egui::Stroke::new(1.0, t.text);
        vis.widgets.hovered.bg_fill = t.card2;
        vis.widgets.hovered.weak_bg_fill = t.card2;
        vis.widgets.active.fg_stroke = egui::Stroke::new(1.0, t.text);
        vis.widgets.active.bg_fill = t.card2;
        vis.widgets.active.weak_bg_fill = t.card2;
        vis.widgets.open.fg_stroke = egui::Stroke::new(1.0, t.text);
        vis.widgets.open.bg_fill = t.card2;
        vis.widgets.open.weak_bg_fill = t.card2;
        vis.selection.stroke = egui::Stroke::new(1.0, t.accent);
        ctx.set_visuals(vis);
    }

    fn save_prefs(&self) {
        let _ = std::fs::write(
            prefs_file(),
            format!(
                "dark={}\nzoom={}\nautoscale={}\nscalecap={}\nstartmin={}\ntheme={}",
                self.theme_kind == ThemeKind::Dark,
                self.zoom,
                self.auto_scale as i32,
                self.scale_cap,
                self.start_minimized as i32,
                match self.theme_kind {
                    ThemeKind::Dark => "dark",
                    ThemeKind::Light => "light",
                    ThemeKind::Elite => "elite",
                }
            ),
        );
    }

    /// Universal icon for pseudo-apps (unknown:pidN / name:...) that have no exe.
    fn is_pseudo_app(name: &str) -> bool {
        name.starts_with("unknown:") || name.starts_with("name:")
    }

    fn unknown_texture(&mut self, ctx: &egui::Context) -> egui::TextureHandle {
        if let Some(tex) = self.icon_tex.get(&i64::MIN) {
            return tex.clone();
        }
        const PNG: &[u8] = include_bytes!("../../assets/unknown.png");
        let img = image::load_from_memory(PNG).expect("embedded unknown.png").to_rgba8();
        let size = [img.width() as usize, img.height() as usize];
        let color_img = egui::ColorImage::from_rgba_unmultiplied(size, &img.into_raw());
        let tex = ctx.load_texture("unknown-app", color_img, egui::TextureOptions::LINEAR);
        self.icon_tex.insert(i64::MIN, tex.clone());
        tex
    }

    /// Icon for an app row: the real icon when one exists, the universal
    /// "?" tile for unknown/unnamed processes, None otherwise (dots for
    /// categories).
    fn app_icon_opt(&mut self, ctx: &egui::Context, id: i64, name: &str) -> Option<egui::TextureHandle> {
        if Self::is_pseudo_app(name) {
            return Some(self.unknown_texture(ctx));
        }
        self.icon_texture(ctx, id)
    }

    fn icon_texture(&mut self, ctx: &egui::Context, id: i64) -> Option<egui::TextureHandle> {
        if let Some(tex) = self.icon_tex.get(&id) {
            return Some(tex.clone());
        }
        let snap = self.snap.lock().unwrap().clone();
        let bytes = snap.icons.get(&id)?;
        if bytes.is_empty() {
            return None;
        }
        let img = image::load_from_memory(bytes).ok()?;
        let rgba = img.to_rgba8();
        let size = [rgba.width() as usize, rgba.height() as usize];
        let color_img = egui::ColorImage::from_rgba_unmultiplied(size, &rgba.into_raw());
        let tex = ctx.load_texture(format!("icon-{}", id), color_img, egui::TextureOptions::LINEAR);
        self.icon_tex.insert(id, tex.clone());
        Some(tex)
    }

    fn nav(&mut self, ctx: &egui::Context) {
        let t = self.theme();
        let unseen = self.snap.lock().unwrap().unseen;
        egui::TopBottomPanel::top("nav")
            .frame(egui::Frame::none().fill(t.nav_bg).inner_margin(egui::Margin::symmetric(16.0, 8.0)))
            .show(ctx, |ui| {
                ui.horizontal(|ui| {
                    ui.label(egui::RichText::new("Packetly").size(17.0).strong().color(t.text));
                    ui.add_space(30.0);
                    ui.with_layout(egui::Layout::left_to_right(egui::Align::Center), |ui| {
                        for tab in [Tab::Graph, Tab::Traffic, Tab::Usage, Tab::Alerts] {
                            let mut label = tab.label().to_string();
                            if tab == Tab::Alerts && unseen > 0 {
                                label = format!("{} ({})", label, unseen);
                            }
                            if ui
                                .selectable_label(self.tab == tab, egui::RichText::new(label).strong())
                                .clicked()
                            {
                                self.tab = tab;
                                let _ = self.tx.send(UiMsg::Refresh(self.tab, self.sel.clone(), self.period));
                            }
                            ui.add_space(14.0);
                        }
                    });
                    let selection_paused = self.tab == Tab::Graph
                        && self.snap.lock().unwrap().sel.is_some();
                    ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                        if selection_paused {
                            ui.colored_label(
                                egui::Color32::from_rgb(240, 200, 60),
                                egui::RichText::new("⏸ paused").size(12.0),
                            );
                            ui.add_space(10.0);
                        }
                        if ui
                            .button(if self.settings_open { "✕ close" } else { "⚙ settings" })
                            .clicked()
                        {
                            self.settings_open = !self.settings_open;
                        }
                        ui.add_space(8.0);

                    });
                });
            });
    }

    fn range_bar(&mut self, ctx: &egui::Context) {
        let t = self.theme();
        let on_usage = self.tab == Tab::Usage;
        let custom_active = if on_usage {
            self.period == UsagePeriod::Custom
        } else {
            matches!(self.sel, RangeSel::Custom(_, _))
        };
        egui::TopBottomPanel::top("rangebar")
            .frame(egui::Frame::none().fill(t.bg).inner_margin(egui::Margin::symmetric(16.0, 2.0)))
            .show(ctx, |ui| {
                ui.horizontal(|ui| {
                    ui.weak("Filter");
                    ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                        let custom_resp = ui.selectable_label(custom_active, "Custom…");
                        if custom_resp.clicked() {
                            if on_usage {
                                self.period = UsagePeriod::Custom;
                            }
                            self.show_custom = !self.show_custom;
                            if self.show_custom {
                                self.cal_anchor = None;
                                let base = match &self.sel {
                                    RangeSel::Custom(_, t) => *t,
                                    _ => chrono::Local::now().timestamp(),
                                };
                                use chrono::{Datelike, TimeZone};
                                let dt = chrono::Local
                                    .timestamp_opt(base, 0)
                                    .single()
                                    .unwrap_or_default();
                                self.cal_view = (dt.year(), dt.month());
                            }
                            let _ = self.tx.send(UiMsg::Refresh(
                                self.tab,
                                self.sel.clone(),
                                self.period,
                            ));
                        }
                        if self.show_custom {
                            let rect = custom_resp.rect;
                            egui::Area::new(egui::Id::new("custom-calendar"))
                                .order(egui::Order::Foreground)
                                .fixed_pos(egui::Pos2::new(
                                    (rect.right() - 276.0).max(8.0),
                                    rect.bottom() + 6.0,
                                ))
                                .show(ctx, |ui| {
                                    egui::Frame::popup(ui.style()).show(ui, |ui| {
                                        ui.set_min_width(264.0);
                                        calendar_body(ui, self);
                                    });
                                });
                        }
                        ui.add_space(6.0);
                        if on_usage {
                            egui::ComboBox::from_id_source("range_usage")
                                .selected_text(self.period.label())
                                .show_ui(ui, |ui| {
                                    for pk in UsagePeriod::all() {
                                        if ui.selectable_label(self.period == pk, pk.label()).clicked() {
                                            self.period = pk;
                                            self.show_custom = false;
                                            let _ = self.tx.send(UiMsg::Refresh(
                                                self.tab,
                                                self.sel.clone(),
                                                self.period,
                                            ));
                                        }
                                    }
                                });
                        } else {
                            egui::ComboBox::from_id_source("range")
                                .selected_text(self.sel.label())
                                .show_ui(ui, |ui| {
                                    for r in Range::all() {
                                        let is_sel = matches!(&self.sel, RangeSel::Preset(p) if *p == r);
                                        if ui.selectable_label(is_sel, r.label()).clicked() {
                                            self.sel = RangeSel::Preset(r);
                                            self.show_custom = false;
                                            let _ = self.tx.send(UiMsg::Refresh(
                                                self.tab,
                                                self.sel.clone(),
                                                self.period,
                                            ));
                                        }
                                    }
                                });
                        }
                    });
                });

            });
    }

    fn summary_bar(&mut self, ctx: &egui::Context) {
        let t = self.theme();
        egui::TopBottomPanel::bottom("summary")
            .frame(egui::Frame::none().fill(t.card).inner_margin(egui::Margin::symmetric(16.0, 8.0)))
            .show(ctx, |ui| {
                let snap = self.snap.lock().unwrap().clone();
                let span_secs = snap
                    .traffic
                    .as_ref()
                    .map(|t| (t.to - t.from).max(1.0))
                    .unwrap_or(1.0);
                let total_in = snap.lanwan.lan_in + snap.lanwan.wan_in;
                let total_out = snap.lanwan.lan_out + snap.lanwan.wan_out;

                ui.horizontal(|ui| {
                    let total = total_in + total_out;
                    let frac = if total > 0.0 { total_in / total } else { 0.5 } as f32;
                    let cx = ui.cursor().left() + 34.0;
                    let cy = ui.cursor().top() + 26.0;
                    let r = 22.0;
                    let painter = ui.painter();
                    let segs = 40;
                    let start = -220.0_f32.to_radians();
                    let sweep = 260.0_f32.to_radians();
                    let mut pink = Vec::new();
                    let mut yellow = Vec::new();
                    for i in 0..=segs {
                        let a = start + sweep * (i as f32 / segs as f32);
                        let pt = egui::Pos2::new(cx + r * a.cos(), cy + r * a.sin());
                        let f = i as f32 / segs as f32;
                        if f <= frac {
                            pink.push(pt);
                        } else {
                            yellow.push(pt);
                        }
                    }
                    if pink.len() > 1 {
                        painter.add(egui::Shape::line(pink, egui::Stroke::new(4.0, t.in_c)));
                    }
                    if yellow.len() > 1 {
                        painter.add(egui::Shape::line(yellow, egui::Stroke::new(4.0, t.out_c)));
                    }
                    ui.allocate_space(egui::vec2(74.0, 54.0));

                    ui.vertical(|ui| {
                        ui.label(
                            egui::RichText::new(format!("down {}", fmt_b(total_in)))
                                .size(15.0)
                                .color(t.in_c)
                                .strong(),
                        );
                        ui.weak(egui::RichText::new(fmt_rate(total_in, span_secs)).size(11.0).color(t.dim));
                    });
                    ui.vertical(|ui| {
                        ui.label(
                            egui::RichText::new(format!("up {}", fmt_b(total_out)))
                                .size(15.0)
                                .color(t.out_c)
                                .strong(),
                        );
                        ui.weak(egui::RichText::new(fmt_rate(total_out, span_secs)).size(11.0).color(t.dim));
                    });
                    ui.add_space(24.0);

                    ui.vertical(|ui| {
                        let grand = (total_in + total_out).max(1.0);
                        for (label, i, o, color) in [
                            ("WAN", snap.lanwan.wan_in, snap.lanwan.wan_out, t.out_c),
                            ("LAN", snap.lanwan.lan_in, snap.lanwan.lan_out, t.in_c),
                        ] {
                            ui.horizontal(|ui| {
                                ui.weak(egui::RichText::new(label).size(12.0));
                                ui.weak(fmt_b(i + o));
                                let w = ui.available_width() - 4.0;
                                let (rect, _) =
                                    ui.allocate_exact_size(egui::vec2(w, 5.0), egui::Sense::hover());
                                let painter = ui.painter();
                                painter.rect_filled(rect, 2.5, t.card2);
                                let share = ((i + o) / grand).min(1.0) as f32;
                                if share > 0.0 {
                                    painter.rect_filled(
                                        egui::Rect::from_min_size(
                                            rect.left_top(),
                                            egui::vec2(rect.width() * share, 5.0),
                                        ),
                                        2.5,
                                        color,
                                    );
                                }
                            });
                        }

                        if let Some(traffic) = &snap.traffic {
                            // mini timeline is cached per data version; sharper smoothing
                            // + dual upload/download silhouettes match the main chart
                            let ver = traffic_ver(traffic);
                            let mini_series = {
                                let mut cache = self.cache.lock().unwrap();
                                let stale =
                                    cache.mini.as_ref().map_or(true, |(v, _)| *v != ver);
                                if stale {
                                    let p = pivot(traffic, t.in_c, t.out_c);
                                    let mr = ((p.n as f64) / 110.0)
                                        .round()
                                        .clamp(1.0, 120.0) as usize;
                                    let r2 = (mr * 2 / 3).max(1);
                                    let r3 = (mr / 3).max(1);
                                    let mut curves: Vec<Vec<[f64; 2]>> = p
                                        .series
                                        .iter()
                                        .map(|s| {
                                            smooth(
                                                &moving_avg(
                                                    &moving_avg(&moving_avg(&s.points, mr), r2),
                                                    r3,
                                                ),
                                                420,
                                            )
                                        })
                                        .collect();
                                    let mut m_max = 0.0_f64;
                                    for pts in &curves {
                                        for pt in pts {
                                            m_max = m_max.max(pt[1]);
                                        }
                                    }
                                    if m_max > 0.0 && p.y_max > 0.0 {
                                        let mk = p.y_max / m_max;
                                        for pts in &mut curves {
                                            for pt in pts.iter_mut() {
                                                pt[1] *= mk;
                                            }
                                        }
                                    }
                                    cache.mini = Some((ver, Arc::new(curves)));
                                }
                                cache.mini.as_ref().unwrap().1.clone()
                            };
                            let mini_colors = [t.out_c, t.in_c];
                            Plot::new("mini_timeline")
                                .height(42.0)
                                .width(ui.available_width())
                                .show_axes([false, false])
                                .show_grid(egui::Vec2b::FALSE)
                                .set_margin_fraction(egui::Vec2::ZERO)
                                .allow_drag(false)
                                .allow_zoom(false)
                                .allow_scroll(false)
                                .allow_boxed_zoom(false)
                                .show(ui, |plot_ui| {
                                    for (idx, pts) in mini_series.iter().enumerate() {
                                        if !pts.is_empty() {
                                            let c = mini_colors[idx % mini_colors.len()];
                                            plot_ui.line(
                                                Line::new(PlotPoints::from(pts.clone()))
                                                    .color(c)
                                                    .fill(0.0_f32)
                                                    .width(1.1_f32),
                                            );
                                        }
                                    }
                                });
                        }
                    });
                });
            });
}

    fn graph_tab(&mut self, ctx: &egui::Context) {
        let t = self.theme();
        egui::CentralPanel::default()
            .frame(egui::Frame::none().fill(t.bg).inner_margin(egui::Margin::symmetric(16.0, 4.0)))
            .show(ctx, |ui| {
                let snap = self.snap.lock().unwrap().clone();

                let ver = snap.traffic.as_ref().map(traffic_ver);
                {
                    let mut cache = self.cache.lock().unwrap();
                    if ver != Some(cache.version) {
                        if let Some(tr) = &snap.traffic {
                            let p = pivot(tr, t.in_c, t.out_c);
                            cache.version = ver.unwrap_or(0);
                            cache.pivot = if p.series.is_empty() { None } else { Some(Arc::new(p)) };
                            cache.rendered = None;
                            cache.mini = None;
                        }
                    }
                }
                let p = self.cache.lock().unwrap().pivot.clone();

                ui.horizontal(|ui| {
                    let lbl = if self.auto_scale {
                        "Auto-scale".to_string()
                    } else {
                        format!("cap {}", fmt_b(self.scale_cap))
                    };
                    let resp = ui.button(format!("{lbl} ▾"));
                    if resp.clicked() {
                        self.show_scale = !self.show_scale;
                    }
                    if self.show_scale {
                        egui::Area::new(egui::Id::new("scale-menu"))
                            .order(egui::Order::Foreground)
                            .fixed_pos(egui::Pos2::new(
                                resp.rect.left(),
                                resp.rect.bottom() + 4.0,
                            ))
                            .show(ctx, |ui| {
                                egui::Frame::popup(ui.style()).show(ui, |ui| {
                                    ui.set_min_width(200.0);
                                    if ui
                                        .checkbox(&mut self.auto_scale, "Auto-Scale")
                                        .changed()
                                    {
                                        self.save_prefs();
                                    }
                                    ui.weak(
                                        egui::RichText::new(
                                            "Uncheck Auto-Scale to set the desired graph scale manually.",
                                        )
                                        .size(10.5),
                                    );
                                    if !self.auto_scale {
                                        ui.separator();
                                        egui::ScrollArea::vertical()
                                            .max_height(240.0)
                                            .show(ui, |ui| {
                                                for (lbl2, val) in [
                                                    ("50 KB", 51_200.0_f64),
                                                    ("100 KB", 102_400.0),
                                                    ("250 KB", 256_000.0),
                                                    ("500 KB", 512_000.0),
                                                    ("1 MB", 1_048_576.0),
                                                    ("2 MB", 2_097_152.0),
                                                    ("5 MB", 5_242_880.0),
                                                    ("10 MB", 10_485_760.0),
                                                    ("25 MB", 26_214_400.0),
                                                    ("50 MB", 52_428_800.0),
                                                    ("100 MB", 104_857_600.0),
                                                    ("250 MB", 262_144_000.0),
                                                    ("500 MB", 524_288_000.0),
                                                    ("1 GB", 1_073_741_824.0),
                                                    ("2 GB", 2_147_483_648.0),
                                                    ("5 GB", 5_368_709_120.0),
                                                ] {
                                                    if ui
                                                        .selectable_label(
                                                            (self.scale_cap - val).abs() < 1.0,
                                                            lbl2,
                                                        )
                                                        .clicked()
                                                    {
                                                        self.scale_cap = val;
                                                        self.save_prefs();
                                                    }
                                                }
                                            });
                                    }
                                });
                            });
                    }
                });

                egui::Frame::none()
                    .fill(t.plot_bg)
                    .rounding(6.0)
                    .show(ui, |ui| {
                        match (&snap.traffic, &p) {
                            (Some(_), Some(p)) if !p.series.is_empty() => {
                                let axis_mode = axis_mode_for(&self.sel, p.x1 - p.x0);
                                let x_step = self.sel.x_step(p.x1 - p.x0);
                                let cap = if self.auto_scale {
                                    f64::INFINITY
                                } else {
                                    self.scale_cap
                                };
                                // Sharper proportional 3-pass smoothing (~1.3%-1.8% FWHM across
                                // every range) preserves distinct bursts & valleys like GlassWire
                                // instead of merging nearby hills into a single vague blob.
                                let r = ((p.n as f64) / 130.0).round().clamp(1.0, 120.0) as usize;
                                let (rendered, target_k) = {
                                    let mut cache = self.cache.lock().unwrap();
                                    let stale = cache.rendered.as_ref().map_or(true, |rd| {
                                        rd.ver != ver.unwrap_or(0) || rd.r != r
                                    });
                                    if stale {
                                        let r2 = (r * 2 / 3).max(1);
                                        let r3 = (r / 3).max(1);
                                        let series_pts: Vec<Vec<[f64; 2]>> = p
                                            .series
                                            .iter()
                                            .map(|s| {
                                                smooth(
                                                    &moving_avg(
                                                        &moving_avg(
                                                            &moving_avg(&s.points, r),
                                                            r2,
                                                        ),
                                                        r3,
                                                    ),
                                                    1400,
                                                )
                                            })
                                            .collect();
                                        let mut rendered_max = 0.0_f64;
                                        for pts in &series_pts {
                                            for pt in pts {
                                                rendered_max = rendered_max.max(pt[1]);
                                            }
                                        }
                                        let tk = if rendered_max > 0.0 && p.y_max > 0.0 {
                                            p.y_max / rendered_max
                                        } else {
                                            1.0
                                        };
                                        cache.rendered = Some(Rendered {
                                            ver: ver.unwrap_or(0),
                                            r,
                                            target_k: tk,
                                            series: Arc::new(series_pts),
                                        });
                                    }
                                    let rd = cache.rendered.as_ref().unwrap();
                                    (rd.series.clone(), rd.target_k)
                                };

                                // Scale from the RAW bucket peak (p.y_max): the rendered curve
                                // is peak-normalized to it so the y-axis stays honest.
                                let target_y_top = if self.auto_scale {
                                    let target = (p.y_max * 1.25).max(1024.0);
                                    let st = nice_step(target / 8.0);
                                    (target / st).ceil() * st
                                } else {
                                    self.scale_cap
                                };

                                // ---- 60fps continuous viewport glide & smooth Y-scale easing ----
                                let now_inst = std::time::Instant::now();
                                let dt = self
                                    .anim_last
                                    .map(|prev| {
                                        now_inst.duration_since(prev).as_secs_f64().clamp(0.0, 0.05)
                                    })
                                    .unwrap_or(0.0);
                                self.anim_last = Some(now_inst);

                                if (p.x1 - self.snap_x1_seen).abs() > 0.01 {
                                    self.snap_x1_seen = p.x1;
                                    self.snap_x1_at = Some(now_inst);
                                }

                                let sel_key = self.sel.query();
                                let is_live_5m = matches!(self.sel, RangeSel::Preset(Range::M5));
                                let is_custom = matches!(self.sel, RangeSel::Custom(_, _));
                                let is_paused = snap.sel.is_some() || self.sel_drag.is_some();
                                let span_full = (p.x1 - p.x0).max(p.bucket);
                                // On 5m, TrafficWriter flushes closed seconds up to now-1, so
                                // keeping the right edge ~2.25s behind p.x1 (capped <= p.x1-1.0)
                                // ensures real closed smoothed data glides seamlessly in from
                                // the right edge at constant 1.0x speed with zero drop-to-zero.
                                let live_lag = if is_live_5m && span_full > 6.0 {
                                    2.25
                                } else {
                                    0.0
                                };
                                let live_ceil = if is_live_5m && span_full > 6.0 {
                                    p.x1 - 1.0
                                } else {
                                    p.x1
                                };
                                let vis_span = if is_live_5m && span_full > 6.0 {
                                    (span_full - 3.0).max(1.0)
                                } else {
                                    span_full
                                };
                                let min_x1 = (p.x0 + p.bucket).min(live_ceil);
                                let range_changed = sel_key != self.anim_sel_key
                                    || self.anim_x1 < p.x0
                                    || self.anim_x1 > p.x1 + p.bucket * 4.0;

                                let mut need_repaint = false;
                                if range_changed {
                                    self.anim_sel_key = sel_key;
                                    self.anim_x1 = (p.x1 - live_lag).clamp(min_x1, live_ceil);
                                    self.anim_y_top = target_y_top;
                                    self.anim_k = target_k;
                                    self.snap_x1_seen = p.x1;
                                    self.snap_x1_at = Some(now_inst);
                                } else if !is_paused {
                                    if is_live_5m && span_full > 6.0 {
                                        let elapsed = self
                                            .snap_x1_at
                                            .map(|t0| {
                                                now_inst.duration_since(t0).as_secs_f64().min(2.5)
                                            })
                                            .unwrap_or(0.0);
                                        let clock_x1 = (p.x1 - live_lag + elapsed).min(live_ceil);
                                        let err = clock_x1 - self.anim_x1;
                                        let speed = (1.0 + err * 1.4).clamp(0.0, 2.2);
                                        self.anim_x1 = (self.anim_x1 + speed * dt).min(live_ceil);
                                        if self.anim_x1 < live_ceil - 0.01 {
                                            need_repaint = true;
                                        }
                                    } else if !is_custom {
                                        let diff_x = p.x1 - self.anim_x1;
                                        if diff_x.abs() > 0.05 {
                                            let ease = 1.0 - (-dt * 7.0).exp();
                                            self.anim_x1 += diff_x * ease;
                                            need_repaint = true;
                                        } else {
                                            self.anim_x1 = p.x1;
                                        }
                                    } else {
                                        self.anim_x1 = p.x1;
                                    }

                                    let ease_y = 1.0 - (-dt * 8.0).exp();
                                    if (target_y_top - self.anim_y_top).abs()
                                        > target_y_top * 0.002
                                    {
                                        self.anim_y_top +=
                                            (target_y_top - self.anim_y_top) * ease_y;
                                        need_repaint = true;
                                    } else {
                                        self.anim_y_top = target_y_top;
                                    }
                                    if (target_k - self.anim_k).abs()
                                        > target_k.max(1.0) * 0.002
                                    {
                                        self.anim_k += (target_k - self.anim_k) * ease_y;
                                        need_repaint = true;
                                    } else {
                                        self.anim_k = target_k;
                                    }
                                }

                                let view_x1 = self.anim_x1.clamp(min_x1, live_ceil);
                                let view_x0 = (view_x1 - vis_span).max(p.x0);
                                let y_top = self.anim_y_top.max(1024.0);
                                let y_step = nice_step(target_y_top / 8.0);
                                let cur_k = self.anim_k;
                                if need_repaint {
                                    ctx.request_repaint_after(Duration::from_millis(16));
                                }

                                let visible_series: Vec<Vec<[f64; 2]>> = rendered
                                    .iter()
                                    .map(|pts| clip_and_scale(pts, view_x0, view_x1, cur_k, cap))
                                    .collect();

                                let plot = Plot::new("traffic")
                                    .x_grid_spacer(move |input| {
                                        time_ticks(input.bounds.0, input.bounds.1, x_step)
                                            .into_iter()
                                            .map(|ts| egui_plot::GridMark {
                                                value: ts,
                                                step_size: x_step,
                                            })
                                            .collect()
                                    })
                                    .y_grid_spacer(move |input| {
                                        let a = (input.bounds.0 / y_step).ceil() as i64;
                                        let b = (input.bounds.1 / y_step).ceil() as i64;
                                        (a..=b)
                                            .map(|i| egui_plot::GridMark {
                                                value: i as f64 * y_step,
                                                step_size: y_step,
                                            })
                                            .collect()
                                    })
                                    .x_axis_formatter(|_, _| String::new())
                                    .y_axis_formatter(|_, _| String::new())
                                    .label_formatter(|_, _| String::new())
                                    .allow_drag(false)
                                    .allow_zoom(false)
                                    .allow_scroll(false)
                                    .show_grid(egui::Vec2b::FALSE)
                                    .set_margin_fraction(egui::Vec2::ZERO)
                                    .show_axes([false, true])
                                    .height((ui.available_height() * 0.88).max(300.0));
                                let plot = plot
                                    .include_x(view_x0)
                                    .include_x(view_x1)
                                    .include_y(0.0)
                                    .include_y(y_top * 1.04);
                                let dark_theme = t.text.r() > 128;
                                let presp = plot.show(ui, |plot_ui| {
                                    // luminous fills first (baseline y=0.0), crisp strokes on top
                                    for (s, pts) in p.series.iter().zip(visible_series.iter()) {
                                        let fill_color = if dark_theme {
                                            egui::Color32::from_rgb(
                                                (s.color.r() as f32 * 0.56) as u8,
                                                (s.color.g() as f32 * 0.56) as u8,
                                                (s.color.b() as f32 * 0.56) as u8,
                                            )
                                        } else {
                                            Theme::alpha(s.color, 95)
                                        };
                                        plot_ui.line(
                                            Line::new(PlotPoints::from(pts.clone()))
                                                .color(fill_color)
                                                .fill(0.0_f32)
                                                .width(0.0_f32),
                                        );
                                    }
                                    for (s, pts) in p.series.iter().zip(visible_series.iter()) {
                                        plot_ui.line(
                                            Line::new(PlotPoints::from(pts.clone()))
                                                .name(s.name.clone())
                                                .color(s.color)
                                                .width(1.6_f32),
                                        );
                                    }
                                });

                                {
                                    let rect = presp.transform.frame();
                                    let painter = ui.painter_at(rect);
                                    let vgrid = Theme::alpha(
                                        t.grid,
                                        ((t.grid.a() as f32) * 0.65).round().max(3.0) as u8,
                                    );
                                    for ts in time_ticks(view_x0, view_x1, x_step) {
                                        let px = presp.transform.position_from_point_x(ts);
                                        painter.line_segment(
                                            [
                                                egui::Pos2::new(px, rect.top()),
                                                egui::Pos2::new(px, rect.bottom() - 20.0),
                                            ],
                                            egui::Stroke::new(1.0, vgrid),
                                        );
                                        painter.text(
                                            egui::Pos2::new(px, rect.bottom() - 8.0),
                                            egui::Align2::CENTER_BOTTOM,
                                            fmt_time_axis(ts, axis_mode),
                                            egui::FontId::proportional(11.0),
                                            t.axis,
                                        );
                                    }
                                    let mut yv = y_step;
                                    while yv <= y_top * 1.02 {
                                        let py = presp.transform.position_from_point_y(yv);
                                        if py >= rect.top() && py <= rect.bottom() {
                                            painter.line_segment(
                                                [
                                                    egui::Pos2::new(rect.left(), py),
                                                    egui::Pos2::new(rect.right(), py),
                                                ],
                                                egui::Stroke::new(1.0, t.grid),
                                            );
                                            // labels hugging the top edge collide with the
                                            // Auto-scale/cap button row above the plot
                                            if py >= rect.top() + 10.0 {
                                                painter.text(
                                                    egui::Pos2::new(rect.left() + 6.0, py - 4.0),
                                                    egui::Align2::LEFT_BOTTOM,
                                                    fmt_b(yv),
                                                    egui::FontId::proportional(11.0),
                                                    t.axis,
                                                );
                                            }
                                        }
                                        yv += y_step;
                                    }
                                }

                                {
                                    let rect = presp.transform.frame();
                                    let painter = ui.painter_at(rect);
                                    for e in &snap.events {
                                        if e.kind < 2 || e.ts < view_x0 || e.ts > view_x1 {
                                            continue;
                                        }
                                        let px = presp.transform.position_from_point_x(e.ts);
                                        let py = rect.top() + 12.0;
                                        painter.circle_filled(egui::Pos2::new(px, py), 6.0, t.danger);
                                        painter.circle_stroke(
                                            egui::Pos2::new(px, py),
                                            6.0,
                                            egui::Stroke::new(1.5, t.card),
                                        );
                                    }
                                }

                                // ---- drag selection over the plot ----
                                {
                                    let frame = presp.transform.frame();
                                    let pointer = ctx.pointer_latest_pos();
                                    let pressed = ctx.input(|i| i.pointer.primary_pressed());
                                    let down = ctx.input(|i| i.pointer.primary_down());
                                    let released = ctx.input(|i| i.pointer.primary_released());
                                    // only interact with the plot when the pointer is over
                                    // the PLOT's own layer - clicks on the selection popup /
                                    // details popup / combo menus (which overlap the plot
                                    // rect) must not start or clear a selection
                                    let over_plot = pointer
                                        .map(|pos| {
                                            ctx.layer_id_at(pos).map_or(false, |l| {
                                                l == presp.response.layer_id
                                            })
                                        })
                                        .unwrap_or(false);
                                    if pressed && over_plot {
                                        if let Some(pos) = pointer {
                                            if frame.contains(pos) {
                                                let ts = presp
                                                    .transform
                                                    .value_from_position(pos)
                                                    .x
                                                    .clamp(view_x0, view_x1);
                                                self.sel_drag = Some((ts, ts));
                                            }
                                        }
                                    } else if down && over_plot {
                                        if let (Some(pos), Some((s0, _))) =
                                            (pointer, self.sel_drag)
                                        {
                                            let ts = presp
                                                .transform
                                                .value_from_position(pos)
                                                .x
                                                .clamp(view_x0, view_x1);
                                            self.sel_drag = Some((s0, ts));
                                        }
                                    }
                                    if released {
                                        if let Some((s0, s1)) = self.sel_drag.take() {
                                            if (s1 - s0).abs() >= 1.5 {
                                                let _ = self.tx.send(UiMsg::Selection(Some((
                                                    s0.min(s1),
                                                    s0.max(s1),
                                                ))));
                                            } else {
                                                let _ = self.tx.send(UiMsg::Selection(None));
                                            }
                                        }
                                    }
                                }

                                let band = self
                                    .sel_drag
                                    .map(|(a, b)| (a.min(b), a.max(b)))
                                    .or_else(|| snap.sel.as_ref().map(|s| (s.t0, s.t1)));
                                if let Some((lo, hi)) = band {
                                    let rect = presp.transform.frame();
                                    let painter = ui.painter_at(rect);
                                    let x0px =
                                        presp.transform.position_from_point_x(lo.min(view_x1));
                                    let x1px =
                                        presp.transform.position_from_point_x(hi.max(view_x0));
                                    let alpha = if t.text.r() > 128 { 38u8 } else { 70u8 };
                                    painter.rect_filled(
                                        egui::Rect::from_min_max(
                                            egui::Pos2::new(x0px, rect.top()),
                                            egui::Pos2::new(x1px, rect.bottom()),
                                        ),
                                        2.0,
                                        egui::Color32::from_rgba_unmultiplied(
                                            110, 205, 165, alpha,
                                        ),
                                    );
                                }

                                if let Some(sd) = &snap.sel {
                                    if self.sel_drag.is_none() {
                                        let mut apps: Vec<SelApp> = sd.apps.clone();
                                        match self.sel_sort {
                                            SortKey::Name => apps.sort_by(|a, b| {
                                                a.name.to_lowercase()
                                                    .cmp(&b.name.to_lowercase())
                                            }),
                                            SortKey::Hosts => apps.sort_by(|a, b| {
                                                b.host_count.cmp(&a.host_count)
                                            }),
                                            SortKey::Down => apps.sort_by(|a, b| {
                                                b.in_bytes
                                                    .partial_cmp(&a.in_bytes)
                                                    .unwrap_or(std::cmp::Ordering::Equal)
                                            }),
                                            _ => apps.sort_by(|a, b| {
                                                (b.in_bytes + b.out_bytes)
                                                    .partial_cmp(&(a.in_bytes + a.out_bytes))
                                                    .unwrap_or(std::cmp::Ordering::Equal)
                                            }),
                                        }
                                        let fr = presp.transform.frame();
                                        let pos = egui::Pos2::new(
                                            fr.left() + 12.0,
                                            (fr.bottom() - 300.0).max(fr.top() + 8.0),
                                        );
                                        egui::Area::new(egui::Id::new("sel-popup"))
                                            .order(egui::Order::Foreground)
                                            .fixed_pos(pos)
                                            .show(ctx, |ui| {
                                                egui::Frame::popup(ui.style()).show(ui, |ui| {
                                                    ui.set_min_width(330.0);
                                                    ui.horizontal(|ui| {
                                                        ui.weak(
                                                            egui::RichText::new(format!(
                                                                "{} - {}",
                                                                fmt_time_tooltip(sd.t0),
                                                                fmt_time_tooltip(sd.t1)
                                                            ))
                                                            .size(11.0),
                                                        );
                                                        ui.with_layout(
                                                            egui::Layout::right_to_left(
                                                                egui::Align::Center,
                                                            ),
                                                            |ui| {
                                                                if ui.small_button("✕").clicked() {
                                                                    let _ = self.tx.send(
                                                                        UiMsg::Selection(None),
                                                                    );
                                                                }
                                                            },
                                                        );
                                                    });
                                                    ui.separator();
                                                    ui.horizontal(|ui| {
                                                        ui.weak(
                                                            egui::RichText::new("App")
                                                                .size(10.5)
                                                                .color(t.dim),
                                                        );
                                                        ui.with_layout(
                                                            egui::Layout::right_to_left(
                                                                egui::Align::Center,
                                                            ),
                                                            |ui| {
                                                                ui.weak(
                                                                    egui::RichText::new("Sort")
                                                                        .size(10.5)
                                                                        .color(t.dim),
                                                                );
                                                                egui::ComboBox::from_id_source(
                                                                    "sel_sort",
                                                                )
                                                                .selected_text(
                                                                    self.sel_sort.label(),
                                                                )
                                                                .width(86.0)
                                                                .show_ui(ui, |ui| {
                                                                    for k in SortKey::detail_keys() {
                                                                        ui.selectable_value(
                                                                            &mut self.sel_sort,
                                                                            k,
                                                                            k.label(),
                                                                        );
                                                                    }
                                                                });
                                                            },
                                                        );
                                                    });
                                                    ui.separator();
                                                    egui::ScrollArea::vertical()
                                                        .max_height(190.0)
                                                        .show(ui, |ui| {
                                                            for a in apps.iter().take(20) {
                                                                ui.horizontal(|ui| {
                                                                    if let Some(tex) = self
                                                                        .app_icon_opt(
                                                                            ctx, a.app_id, &a.name,
                                                                        )
                                                                    {
                                                                        ui.add(
                                                                            egui::Image::from_texture(&tex)
                                                                                .max_size(egui::vec2(15.0, 15.0)),
                                                                        );
                                                                    } else {
                                                                        ui.weak("·");
                                                                    }
                                                                    let lbl = ui.add(
                                                                        egui::Label::new(
                                                                            egui::RichText::new(&a.name).size(12.0),
                                                                        )
                                                                        .truncate()
                                                                        .sense(egui::Sense::click()),
                                                                    );
                                                                    if lbl.hovered() {
                                                                        ui.ctx().set_cursor_icon(
                                                                            egui::CursorIcon::PointingHand,
                                                                        );
                                                                    }
                                                                    if lbl.clicked() {
                                                                        let _ = self.tx.send(
                                                                            UiMsg::AppDetail(Some((
                                                                                a.app_id,
                                                                                sd.t0 as i64,
                                                                                sd.t1 as i64,
                                                                            ))),
                                                                        );
                                                                    }
                                                                    ui.weak(
                                                                        egui::RichText::new(format!(
                                                                            "+{}",
                                                                            a.host_count
                                                                        ))
                                                                        .size(10.5)
                                                                        .color(t.dim),
                                                                    );
                                                                    ui.with_layout(
                                                                        egui::Layout::right_to_left(
                                                                            egui::Align::Center,
                                                                        ),
                                                                        |ui| {
                                                                            ui.weak(
                                                                                egui::RichText::new(
                                                                                    format!("↑ {}", fmt_b(a.out_bytes)),
                                                                                )
                                                                                .color(t.out_c),
                                                                            );
                                                                            ui.weak(
                                                                                egui::RichText::new(
                                                                                    format!("↓ {}", fmt_b(a.in_bytes)),
                                                                                )
                                                                                .color(t.in_c),
                                                                            );
                                                                        },
                                                                    );
                                                                });
                                                            }
                                                        });
                                                                                                        if !sd.hosts.is_empty() {
                                                        ui.separator();
                                                        for h in sd.hosts.iter().take(3) {
                                                            ui.horizontal(|ui| {
                                                                if let Some(rg) =
                                                                    h.region.as_deref()
                                                                {
                                                                    flag_ui(ui, rg);
                                                                }
                                                                ui.label(
                                                                    egui::RichText::new(&h.label)
                                                                        .size(11.5),
                                                                );
                                                                ui.with_layout(
                                                                    egui::Layout::right_to_left(
                                                                        egui::Align::Center,
                                                                    ),
                                                                    |ui| {
                                                                        ui.weak(fmt_b(
                                                                            h.in_bytes
                                                                                + h.out_bytes,
                                                                        ));
                                                                    },
                                                                );
                                                            });
                                                        }
                                                    }
                                                });
                                            });
                                    }
                                }

                            }
                            _ => {
                                let msg = if snap.error.is_some() {
                                    "waiting for the collector…"
                                } else {
                                    "no traffic recorded in this range yet"
                                };
                                ui.add_space(ui.available_height() * 0.38);
                                ui.vertical_centered(|ui| ui.weak(msg));
                                ui.add_space(ui.available_height() * 0.38);
                            }
                        }
                    });
            });
}

    fn traffic_tab(&mut self, ctx: &egui::Context) {
        let t = self.theme();
        egui::CentralPanel::default()
            .frame(egui::Frame::none().fill(t.bg).inner_margin(egui::Margin::symmetric(16.0, 4.0)))
            .show(ctx, |ui| {
                let snap = self.snap.lock().unwrap().clone();
                let type_series = snap.type_data.clone().map(|g| group_series(&g)).unwrap_or_default();
                let country_series =
                    snap.country_data.clone().map(|g| group_series(&g)).unwrap_or_default();

                let mut col = |app: &mut PacketlyApp,
                               ui: &mut egui::Ui,
                               title: &str,
                               rows: Vec<(
                    String,
                    Option<String>,
                    egui::Color32,
                    Option<i64>,
                    f64,
                    f64,
                )>| {
                    ui.vertical(|ui| {
                        ui.strong(egui::RichText::new(title).size(13.0).color(t.text));
                        ui.add_space(6.0);
                        egui::ScrollArea::vertical()
                            .auto_shrink([false, false])
                            .show(ui, |ui| {
                                if rows.is_empty() {
                                    ui.weak("no data in this range");
                                    return;
                                }
                                let max =
                                    rows.iter().map(|r| r.4 + r.5).fold(1.0_f64, f64::max);
                                let range = snap
                                    .traffic
                                    .as_ref()
                                    .map(|tr| (tr.from as i64, tr.to as i64));
                                for (name, sub, color, icon_id, i, o) in rows {
                                    ui.horizontal(|ui| {
                                        if let Some(tex) = icon_id
                                            .and_then(|id| app.app_icon_opt(ui.ctx(), id, &name))
                                        {
                                            ui.add(
                                                egui::Image::from_texture(&tex)
                                                    .max_size(egui::vec2(17.0, 17.0)),
                                            );
                                        } else {
                                            ui.colored_label(color, "●");
                                        }
                                        let aw = ui.available_width();
                                        let name_w = (aw - 84.0).max(60.0);
                                        ui.vertical(|ui| {
                                            ui.set_max_width(name_w);
                                            if name.len() == 2 && flag_known(&name) {
                                                flag_ui(ui, &name);
                                            }
                                            let lbl = ui.add(
                                                egui::Label::new(
                                                    egui::RichText::new(&name).color(t.text),
                                                )
                                                .truncate()
                                                .sense(egui::Sense::click()),
                                            );
                                            if icon_id.is_some() {
                                                if lbl.hovered() {
                                                    ui.ctx().set_cursor_icon(
                                                        egui::CursorIcon::PointingHand,
                                                    );
                                                }
                                                if lbl.clicked() {
                                                    if let Some(id) = icon_id {
                                                        let (f, tt) =
                                                            range.unwrap_or((0, 0));
                                                        let _ = app.tx.send(UiMsg::AppDetail(
                                                            Some((id, f, tt)),
                                                        ));
                                                    }
                                                }
                                            }
                                            if let Some(s) = &sub {
                                                flag_ui(ui, s);
                                                ui.weak(egui::RichText::new(s.as_str())
                                                    .size(10.5)
                                                    .color(t.dim));
                                            }
                                        });
                                        ui.with_layout(
                                            egui::Layout::right_to_left(egui::Align::Center),
                                            |ui| {
                                                ui.weak(fmt_b(i + o));
                                            },
                                        );
                                    });
                                    let rect = ui.available_rect_before_wrap();
                                    ui.painter().rect_filled(
                                        egui::Rect::from_min_size(
                                            egui::Pos2::new(rect.left(), rect.top()),
                                            egui::vec2(
                                                rect.width() * ((i + o) / max).min(1.0) as f32,
                                                2.0,
                                            ),
                                        ),
                                        1.0,
                                        Theme::alpha(color, 170),
                                    );
                                    ui.add_space(8.0);
                                }
                            });
                    });
                };

                ui.horizontal(|ui| {
                    ui.strong(egui::RichText::new("Traffic").size(14.0).color(t.text));
                    ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                        ui.weak("Sort");
                        egui::ComboBox::from_id_source("traffic_sort")
                            .selected_text(self.traffic_sort.label())
                            .width(92.0)
                            .show_ui(ui, |ui| {
                                for k in SortKey::all() {
                                    ui.selectable_value(&mut self.traffic_sort, k, k.label());
                                }
                            });
                    });
                });
                ui.add_space(6.0);

                let mut app_rows: Vec<(String, Option<String>, egui::Color32, Option<i64>, f64, f64)> =
                    snap.apps
                        .iter()
                        .map(|a| {
                            (
                                a.name.clone(),
                                None,
                                color_for(&a.id.to_string()),
                                Some(a.id),
                                a.in_bytes,
                                a.out_bytes,
                            )
                        })
                        .collect();
                let mut host_rows: Vec<(String, Option<String>, egui::Color32, Option<i64>, f64, f64)> =
                    snap.hosts
                        .iter()
                        .map(|h| {
                            (
                                h.label.clone(),
                                h.region.clone().filter(|s| !s.is_empty()),
                                color_for(&h.label),
                                None,
                                h.in_bytes,
                                h.out_bytes,
                            )
                        })
                        .collect();
                let mut type_rows: Vec<(String, Option<String>, egui::Color32, Option<i64>, f64, f64)> =
                    type_series
                        .iter()
                        .map(|s| {
                            (
                                s.name.clone(),
                                None,
                                s.color,
                                None,
                                s.points.iter().map(|p| p[1]).sum::<f64>(),
                                0.0,
                            )
                        })
                        .collect();
                let mut country_rows: Vec<(String, Option<String>, egui::Color32, Option<i64>, f64, f64)> =
                    country_series
                        .iter()
                        .map(|s| {
                            (
                                s.name.clone(),
                                None,
                                s.color,
                                None,
                                s.points.iter().map(|p| p[1]).sum::<f64>(),
                                0.0,
                            )
                        })
                        .collect();
                sort_rows(&mut app_rows, self.traffic_sort);
                sort_rows(&mut host_rows, self.traffic_sort);
                sort_rows(&mut type_rows, self.traffic_sort);
                sort_rows(&mut country_rows, self.traffic_sort);

                ui.columns(4, |cols| {
                    col(self, &mut cols[0], "Apps", app_rows);
                    col(self, &mut cols[1], "Hosts", host_rows);
                    col(self, &mut cols[2], "Traffic Type", type_rows);
                    col(self, &mut cols[3], "Countries", country_rows);
                });
            });
}

    fn usage_tab(&mut self, ctx: &egui::Context) {
        let t = self.theme();
        egui::CentralPanel::default()
            .frame(egui::Frame::none().fill(t.bg).inner_margin(egui::Margin::symmetric(16.0, 4.0)))
            .show(ctx, |ui| {
                let snap = self.snap.lock().unwrap().clone();

                ui.horizontal(|ui| {
                    ui.strong("Usage");
                    ui.add_space(10.0);
                    ui.weak(egui::RichText::new(self.period.label()).size(12.0).color(t.dim));
                    ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                        ui.weak(format!("total: {}", fmt_b(snap.usage_total)));
                    });
                });
                ui.add_space(6.0);
                if !snap.quotas.is_empty() {
                    egui::Grid::new("quotas")
                        .num_columns(5)
                        .spacing([12.0, 4.0])
                        .show(ui, |ui| {
                            for q in &snap.quotas {
                                let pct = (q.ratio * 100.0) as i32;
                                let color = if q.ratio >= 1.0 {
                                    t.danger
                                } else if q.ratio >= 0.75 {
                                    t.out_c
                                } else {
                                    t.in_c
                                };
                                ui.label(egui::RichText::new(&q.app_name).color(t.text));
                                ui.weak(format!("{} · {}", q.period, fmt_b(q.limit)));
                                let (rect, _) = ui
                                    .allocate_exact_size(egui::vec2(160.0, 8.0), egui::Sense::hover());
                                ui.painter().rect_filled(rect, 4.0, t.card2);
                                ui.painter().rect_filled(
                                    egui::Rect::from_min_size(
                                        rect.left_top(),
                                        egui::vec2(rect.width() * q.ratio.min(1.0) as f32, 8.0),
                                    ),
                                    4.0,
                                    color,
                                );
                                ui.weak(format!(
                                    "{} / {} ({}%)",
                                    fmt_b(q.used),
                                    fmt_b(q.limit),
                                    pct
                                ));
                                if ui.button("del").on_hover_text("Delete quota").clicked() {
                                    let mut url =
                                        format!("{}/api/quotas?period={}", API, q.period);
                                    if let Some(id) = q.app_id {
                                        url.push_str(&format!("&appId={}", id));
                                    }
                                    let _ = ureq::delete(&url)
                                        .timeout(Duration::from_secs(5))
                                        .call();
                                    let _ = self.tx.send(UiMsg::Refresh(self.tab, self.sel.clone(), self.period));
                                }
                                ui.end_row();
                            }
                        });
                    ui.add_space(8.0);
                }

                ui.horizontal(|ui| {
                    ui.weak("Set quota:");
                    let app_label = if self.quota_app.is_empty() {
                        "All apps (global)".to_string()
                    } else {
                        snap.all_apps
                            .iter()
                            .find(|(id, _)| id.to_string() == self.quota_app)
                            .map(|(_, n)| n.clone())
                            .unwrap_or_else(|| "app".to_string())
                    };
                    egui::ComboBox::from_id_source("quota_app")
                        .selected_text(app_label)
                        .width(170.0)
                        .show_ui(ui, |ui| {
                            ui.selectable_value(
                                &mut self.quota_app,
                                String::new(),
                                "All apps (global)",
                            );
                            for (id, name) in &snap.all_apps {
                                ui.selectable_value(&mut self.quota_app, id.to_string(), name);
                            }
                        });
                    egui::ComboBox::from_id_source("quota_period")
                        .selected_text(match self.quota_period.as_str() {
                            "weekly" => "Weekly",
                            "monthly" => "Monthly",
                            _ => "Daily",
                        })
                        .show_ui(ui, |ui| {
                            ui.selectable_value(&mut self.quota_period, "daily".into(), "Daily");
                            ui.selectable_value(&mut self.quota_period, "weekly".into(), "Weekly");
                            ui.selectable_value(&mut self.quota_period, "monthly".into(), "Monthly");
                        });
                    ui.add(
                        egui::TextEdit::singleline(&mut self.quota_value)
                            .desired_width(70.0)
                            .hint_text("Limit"),
                    );
                    egui::ComboBox::from_id_source("quota_unit")
                        .selected_text(if self.quota_unit == 1073741824 { "GB" } else { "MB" })
                        .width(60.0)
                        .show_ui(ui, |ui| {
                            ui.selectable_value(&mut self.quota_unit, 1048576, "MB");
                            ui.selectable_value(&mut self.quota_unit, 1073741824, "GB");
                        });
                    if ui.button("Set quota").clicked() {
                        if let Ok(n) = self.quota_value.trim().parse::<f64>() {
                            if n > 0.0 {
                                let body = serde_json::json!({
                                    "appId": if self.quota_app.is_empty() {
                                        serde_json::Value::Null
                                    } else {
                                        serde_json::json!(self.quota_app.parse::<i64>().unwrap_or(0))
                                    },
                                    "period": self.quota_period,
                                    "limitBytes": (n * self.quota_unit as f64) as i64,
                                });
                                let _ = ureq::post(&format!("{}/api/quotas", API))
                                    .timeout(Duration::from_secs(5))
                                    .send_json(body)
                                    .ok();
                                self.quota_value.clear();
                                let _ = self.tx.send(UiMsg::Refresh(self.tab, self.sel.clone(), self.period));
                            }
                        }
                    }
                });
                ui.add_space(6.0);
                ui.separator();
                ui.add_space(4.0);

                egui::ScrollArea::vertical()
                    .auto_shrink([false, false])
                    .max_height(380.0)
                    .show(ui, |ui| {
                        if snap.usage_per_app.is_empty() {
                            ui.weak("no usage recorded in this period");
                            return;
                        }
                        let max = snap
                            .usage_per_app
                            .iter()
                            .map(|r| r.2)
                            .fold(1.0_f64, f64::max);
                        let (ur_f, ur_t) = snap.usage_range;
                        for (id, name, bytes) in &snap.usage_per_app {
                            let c = color_for(&id.to_string());
                            ui.horizontal(|ui| {
                                if let Some(tex) =
                                    self.app_icon_opt(ui.ctx(), *id, name)
                                {
                                    ui.add(
                                        egui::Image::from_texture(&tex)
                                            .max_size(egui::vec2(17.0, 17.0)),
                                    );
                                } else {
                                    ui.colored_label(c, "●");
                                }
                                let lbl = ui.add(
                                    egui::Label::new(egui::RichText::new(name).color(t.text))
                                        .sense(egui::Sense::click()),
                                );
                                if lbl.hovered() {
                                    ui.ctx().set_cursor_icon(egui::CursorIcon::PointingHand);
                                }
                                if lbl.clicked() {
                                    let _ = self.tx.send(UiMsg::AppDetail(Some((
                                        *id, ur_f, ur_t,
                                    ))));
                                }
                                ui.with_layout(
                                    egui::Layout::right_to_left(egui::Align::Center),
                                    |ui| {
                                        ui.weak(fmt_b(*bytes));
                                    },
                                );
                            });
                            let rect = ui.available_rect_before_wrap();
                            ui.painter().rect_filled(
                                egui::Rect::from_min_size(
                                    egui::Pos2::new(rect.left(), rect.top()),
                                    egui::vec2(rect.width() * (bytes / max).min(1.0) as f32, 2.5),
                                ),
                                1.0,
                                Theme::alpha(c, 170),
                            );
                            ui.add_space(6.0);
                        }
                    });
            });
}

    fn alerts_tab(&mut self, ctx: &egui::Context) {
        let t = self.theme();
        egui::CentralPanel::default()
            .frame(egui::Frame::none().fill(t.bg).inner_margin(egui::Margin::symmetric(16.0, 4.0)))
            .show(ctx, |ui| {
                let snap = self.snap.lock().unwrap().clone();
                ui.horizontal(|ui| {
                    ui.strong("Alerts");
                    ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                        if ui.button("Mark all seen").clicked() {
                            let _ = ureq::post(&format!("{}/api/events/seen", API)).call();
                            let _ = self.tx.send(UiMsg::Refresh(self.tab, self.sel.clone(), self.period));
                        }
                    });
                });
                ui.add_space(6.0);
                egui::ScrollArea::vertical()
                    .auto_shrink([false, false])
                    .show(ui, |ui| {
                        if snap.events.is_empty() {
                            ui.weak("no alerts yet");
                            return;
                        }
                        for e in &snap.events {
                            let (badge, badge_color, text) = match e.kind {
                                1 => {
                                    let name = e.app_name.clone().unwrap_or_else(|| {
                                        serde_json::from_str::<serde_json::Value>(&e.data)
                                            .ok()
                                            .and_then(|d| d["name"].as_str().map(|s| s.to_string()))
                                            .unwrap_or_else(|| "unknown".into())
                                    });
                                    (
                                        "NEW".to_string(),
                                        t.accent,
                                        format!("New app on the network: {}", name),
                                    )
                                }
                                2 | 3 | 4 => {
                                    let pct = match e.kind {
                                        2 => 75,
                                        3 => 90,
                                        _ => 100,
                                    };
                                    let who =
                                        e.app_name.clone().unwrap_or_else(|| "All apps".into());
                                    let d = serde_json::from_str::<serde_json::Value>(&e.data)
                                        .unwrap_or(serde_json::Value::Null);
                                    let used = d["used"].as_f64().unwrap_or(0.0);
                                    let limit = d["limit"].as_f64().unwrap_or(0.0);
                                    let period =
                                        d["period"].as_str().unwrap_or("daily").to_string();
                                    (
                                        format!("{}%", pct),
                                        if e.kind == 4 { t.danger } else { t.out_c },
                                        format!(
                                            "{} reached {}% of its {} quota ({}, {})",
                                            who,
                                            pct,
                                            period,
                                            fmt_b(used),
                                            fmt_b(limit)
                                        ),
                                    )
                                }
                                _ => ("·".to_string(), t.dim, "alert".to_string()),
                            };
                            ui.horizontal(|ui| {
                                ui.label(
                                    egui::RichText::new(format!("{:>4}", badge))
                                        .color(badge_color)
                                        .strong()
                                        .size(11.0),
                                );
                                ui.vertical(|ui| {
                                    ui.label(egui::RichText::new(text).color(t.text));
                                    ui.weak(
                                        egui::RichText::new(rel_time(e.ts)).size(11.0).color(t.dim),
                                    );
                                });
                            });
                            ui.add_space(2.0);
                        }
                    });
            });
}
}

fn prefs_file() -> std::path::PathBuf {
    let dir = std::path::Path::new(
        &std::env::var("LOCALAPPDATA").unwrap_or_else(|_| ".".to_string()),
    )
    .join("Packetly");
    let _ = std::fs::create_dir_all(&dir);
    dir.join("ui-prefs.txt")
}



impl eframe::App for PacketlyApp {
    fn update(&mut self, ctx: &egui::Context, _frame: &mut eframe::Frame) {
        let t = self.theme();
        Self::apply_visuals(ctx, &t);
        self.nav(ctx);
        match self.tab {
            Tab::Graph => {
                self.range_bar(ctx);
                self.summary_bar(ctx);
                self.graph_tab(ctx);
            }
            Tab::Traffic => {
                self.range_bar(ctx);
                self.traffic_tab(ctx);
            }
            Tab::Usage => {
                self.range_bar(ctx);
                self.usage_tab(ctx);
            }
            Tab::Alerts => self.alerts_tab(ctx),
        }
        self.app_detail_popup(ctx);
        self.settings_dialog(ctx);
    }


}

impl PacketlyApp {
    fn settings_dialog(&mut self, ctx: &egui::Context) {
        if !self.settings_open {
            return;
        }
        let t = self.theme();
        let snap = self.snap.lock().unwrap().clone();
        let flags = self.flags.lock().unwrap().clone();

        // one-shot background queries when the dialog opens
        if flags.startup_state.is_none() && !flags.startup_pending {
            let flags2 = self.flags.clone();
            let ctx2 = ctx.clone();
            std::thread::spawn(move || {
                let ok = query_startup();
                flags2.lock().unwrap().startup_state = Some(ok);
                ctx2.request_repaint();
            });
        }
        if flags.geoip_state.is_none() {
            let flags2 = self.flags.clone();
            let ctx2 = ctx.clone();
            std::thread::spawn(move || {
                let st = fetch_geoip_state();
                flags2.lock().unwrap().geoip_state = st;
                ctx2.request_repaint();
            });
        }

        egui::Area::new(egui::Id::new("settings-dialog"))
            .order(egui::Order::Foreground)
            .anchor(egui::Align2::CENTER_CENTER, [0.0, 0.0])
            .show(ctx, |ui| {
                egui::Frame::popup(ui.style())
                    .fill(t.card)
                    .stroke(egui::Stroke::new(1.0, t.border))
                    .show(ui, |ui| {
                        ui.set_min_width(460.0);
                        ui.set_max_width(460.0);

                        ui.horizontal(|ui| {
                            ui.strong(
                                egui::RichText::new("Settings").size(15.0).color(t.text),
                            );
                            ui.with_layout(
                                egui::Layout::right_to_left(egui::Align::Center),
                                |ui| {
                                    if ui.small_button("✕").clicked() {
                                        self.settings_open = false;
                                    }
                                },
                            );
                        });
                        ui.separator();

                        // ---- appearance ----
                        ui.weak(
                            egui::RichText::new("APPEARANCE")
                                .size(10.5)
                                .color(t.dim),
                        );
                        ui.horizontal(|ui| {
                            ui.weak("Theme:");
                            egui::ComboBox::from_id_source("theme_kind")
                                .selected_text(self.theme_kind.label())
                                .width(130.0)
                                .show_ui(ui, |ui| {
                                    for k in ThemeKind::all() {
                                        if ui
                                            .selectable_label(
                                                self.theme_kind == k,
                                                k.label(),
                                            )
                                            .clicked()
                                        {
                                            self.theme_kind = k;
                                            Self::apply_visuals(ctx, &self.theme());
                                            self.save_prefs();
                                        }
                                    }
                                });
                        });
                        ui.horizontal(|ui| {
                            ui.weak("UI zoom:");
                            egui::ComboBox::from_id_source("settings_zoom")
                                .selected_text(format!("{:.0}%", self.zoom * 100.0))
                                .width(80.0)
                                .show_ui(ui, |ui| {
                                    for z in [1.0_f32, 1.25, 1.5, 1.75] {
                                        if ui
                                            .selectable_label(
                                                (self.zoom - z).abs() < 0.01,
                                                format!("{:.0}%", z * 100.0),
                                            )
                                            .clicked()
                                        {
                                            self.zoom = z;
                                            ctx.set_pixels_per_point(z);
                                            self.save_prefs();
                                        }
                                    }
                                });
                        });
                        ui.add_space(4.0);

                        // ---- startup ----
                        ui.weak(
                            egui::RichText::new("STARTUP")
                                .size(10.5)
                                .color(t.dim),
                        );
                        if flags.startup_pending {
                            ui.weak(
                                egui::RichText::new(
                                    "applying… (confirm the UAC prompt if it appears)",
                                )
                                .size(11.0)
                                .color(t.dim),
                            );
                        } else {
                            match flags.startup_state {
                                None => {
                                    ui.weak(
                                        egui::RichText::new("checking…")
                                            .size(11.0)
                                            .color(t.dim),
                                    );
                                }
                                Some(on) => {
                                    let mut v = on;
                                    let resp = ui.add_enabled(
                                        true,
                                        egui::Checkbox::new(&mut v, "Run at Windows startup"),
                                    );
                                    if resp.clicked() {
                                        let flags2 = self.flags.clone();
                                        let ctx2 = ctx.clone();
                                        let enable = !on;
                                        flags2.lock().unwrap().startup_pending = true;
                                        std::thread::spawn(move || {
                                            set_startup(enable);
                                            let ok = query_startup();
                                            flags2.lock().unwrap().startup_state = Some(ok);
                                            flags2.lock().unwrap().startup_pending = false;
                                            ctx2.request_repaint();
                                        });
                                    }
                                }
                            }
                        }
                        let start_min_allowed =
                            flags.startup_state == Some(true) && !flags.startup_pending;
                        let resp = ui.add_enabled(
                            start_min_allowed,
                            egui::Checkbox::new(
                                &mut self.start_minimized,
                                "Start minimized to tray (applies at next startup)",
                            ),
                        );
                        if resp.changed() {
                            self.save_prefs();
                        }
                        if !start_min_allowed && flags.startup_state != Some(true) {
                            ui.weak(
                                egui::RichText::new(
                                    "enable \u{201c}run at windows startup\u{201d} first",
                                )
                                .size(10.0)
                                .color(t.dim),
                            );
                        }
                        if ui
                            .add(
                                egui::Label::new(
                                    egui::RichText::new(
                                        "the collector keeps running in the tray; click its icon to open the dashboard",
                                    )
                                    .size(10.0)
                                    .color(t.dim),
                                ),
                            )
                            .changed()
                        {}
                        ui.add_space(4.0);

                        // ---- privacy ----
                        ui.weak(
                            egui::RichText::new("PRIVACY")
                                .size(10.5)
                                .color(t.dim),
                        );
                        match flags.geoip_state {
                            None => {
                                ui.weak(
                                    egui::RichText::new("checking…")
                                        .size(11.0)
                                        .color(t.dim),
                                );
                            }
                            Some(on) => {
                                let mut v = on;
                                if ui
                                    .checkbox(
                                        &mut v,
                                        "Resolve host countries (GeoIP, uses ip-api.com)",
                                    )
                                    .changed()
                                {
                                    let flags2 = self.flags.clone();
                                    let ctx2 = ctx.clone();
                                    flags2.lock().unwrap().geoip_state = None;
                                    std::thread::spawn(move || {
                                        let st = set_geoip_state(!on);
                                        flags2.lock().unwrap().geoip_state = st;
                                        ctx2.request_repaint();
                                    });
                                }
                            }
                        }
                        ui.add_space(4.0);

                        // ---- about ----
                        ui.weak(
                            egui::RichText::new("ABOUT").size(10.5).color(t.dim),
                        );
                        let row = |ui: &mut egui::Ui, k: &str, v: String| {
                            ui.horizontal(|ui| {
                                ui.set_min_width(430.0);
                                ui.weak(egui::RichText::new(k).size(11.0).color(t.dim));
                                ui.with_layout(
                                    egui::Layout::right_to_left(egui::Align::Center),
                                    |ui| {
                                        ui.add(
                                            egui::Label::new(
                                                egui::RichText::new(v).size(11.0),
                                            )
                                            .truncate(),
                                        );
                                    },
                                );
                            });
                        };
                        row(
                            ui,
                            "Version:",
                            snap.app_version.clone(),
                        );
                        row(ui, "API:", "127.0.0.1:8912".to_string());
                        row(
                            ui,
                            "Data:",
                            format!(
                                "{}\\Packetly",
                                std::env::var("LOCALAPPDATA").unwrap_or_default()
                            ),
                        );
                    });
            });
    }
}

/// Window/taskbar icon, embedded at compile time so every build (GNU or MSVC)
/// shows the Packetly mark instead of egui's default.
fn app_icon() -> std::sync::Arc<egui::IconData> {
    const PNG: &[u8] = include_bytes!("../../assets/packetly.png");
    let img = image::load_from_memory(PNG).expect("embedded packetly.png").to_rgba8();
    std::sync::Arc::new(egui::IconData {
        width: img.width(),
        height: img.height(),
        rgba: img.into_raw(),
    })
}

fn main() -> eframe::Result {
    let guard = std::net::TcpListener::bind("127.0.0.1:8913");
    if guard.is_err() {
        return Ok(());
    }
    let _guard = guard.unwrap();

    let snap: Arc<Mutex<Arc<Snapshot>>> = Arc::new(Mutex::new(Arc::new(Snapshot::default())));
    let flags: Arc<Mutex<UiFlags>> = Arc::new(Mutex::new(UiFlags::default()));
    let (tx, rx) = std::sync::mpsc::channel::<UiMsg>();
    let ui_ctx: Arc<Mutex<Option<egui::Context>>> = Arc::new(Mutex::new(None));

    let mut theme_kind = ThemeKind::Dark;
    let mut zoom = 1.0_f32;
    let mut auto_scale = true;
    let mut scale_cap = 1_048_576.0_f64;
    let mut start_minimized = false;
    if let Ok(s) = std::fs::read_to_string(prefs_file()) {
        for line in s.lines() {
            let mut kv = line.splitn(2, '=');
            let k = kv.next().unwrap_or("");
            let v = kv.next().unwrap_or("");
            match k {
                "dark" => {
                    if theme_kind == ThemeKind::Dark {
                        theme_kind = if v.trim() != "false" {
                            ThemeKind::Dark
                        } else {
                            ThemeKind::Light
                        };
                    }
                }
                "zoom" => zoom = v.trim().parse().unwrap_or(1.0),
                "autoscale" => auto_scale = v.trim() != "0",
                "scalecap" => scale_cap = v.trim().parse().unwrap_or(1_048_576.0),
                "startmin" => start_minimized = v.trim() != "0",
                "theme" => {
                    theme_kind = match v.trim() {
                        "light" => ThemeKind::Light,
                        "elite" => ThemeKind::Elite,
                        _ => ThemeKind::Dark,
                    }
                }
                "dark" => {
                    if theme_kind == ThemeKind::Dark {
                        theme_kind = if v.trim() != "false" {
                            ThemeKind::Dark
                        } else {
                            ThemeKind::Light
                        };
                    }
                }
                _ => {}
            }
        }
    }
    let zoom = zoom.clamp(0.75, 2.0);

    {
        let snap = snap.clone();
        let ui_ctx = ui_ctx.clone();
        let mut icon_cache: HashMap<i64, Vec<u8>> = HashMap::new();
        let mut icon_failed: HashSet<i64> = HashSet::new();
        std::thread::spawn(move || {
            let mut tab = Tab::Graph;
            let mut sel = RangeSel::Preset(Range::M5);
            let mut period = UsagePeriod::Today;
            let mut sel_data: Option<SelData> = None;
            let mut app_detail_req: Option<(i64, i64, i64)> = None;
            let mut paused_dirty = false;
            let mut tick: u64 = 0;
            let mut last_fetch_dur = Duration::ZERO;
            loop {
                let timeout = if tick == 0 {
                    Duration::from_millis(1)
                } else {
                    sel.refresh().saturating_sub(last_fetch_dur)
                };
                match rx.recv_timeout(timeout) {
                    Ok(msg) => match msg {
                        UiMsg::Refresh(t, s, p) => {
                            tab = t;
                            sel = s;
                            period = p;
                            sel_data = None;
                            app_detail_req = None;
                            tick = 0;
                        }
                        UiMsg::AppDetail(s) => {
                            app_detail_req = s;
                        }
                        UiMsg::Selection(s) => match s {
                            None => {
                                sel_data = None;
                            }
                            Some((t0, t1)) => {
                                let hosts = fetch_json(&format!(
                                    "{}/api/hosts?from={}&to={}&limit=4",
                                    API, t0 as i64, t1 as i64
                                ))
                                .ok()
                                .and_then(|d| {
                                    d["hosts"].as_array().map(|arr| {
                                        arr.iter()
                                            .map(|h| HostRow {
                                                label: h["hostname"]
                                                    .as_str()
                                                    .map(|x| x.to_string())
                                                    .unwrap_or_else(|| {
                                                        h["ip"].as_str().unwrap_or("?").to_string()
                                                    }),
                                                region: h["region"]
                                                    .as_str()
                                                    .map(|x| x.to_string()),
                                                in_bytes: h["inBytes"].as_f64().unwrap_or(0.0),
                                                out_bytes: h["outBytes"].as_f64().unwrap_or(0.0),
                                            })
                                            .collect()
                                    })
                                })
                                .unwrap_or_default();
                                // apps with host counts for the selection table
                                let apps = fetch_json(&format!(
                                    "{}/api/applist?from={}&to={}&limit=50",
                                    API, t0 as i64, t1 as i64
                                ))
                                .ok()
                                .and_then(|d| {
                                    d["apps"].as_array().map(|arr| {
                                        arr.iter()
                                            .map(|a| SelApp {
                                                app_id: a["appId"].as_i64().unwrap_or(0),
                                                name: a["name"].as_str().unwrap_or("?").to_string(),
                                                host_count: a["hostCount"].as_i64().unwrap_or(0),
                                                in_bytes: a["inBytes"].as_f64().unwrap_or(0.0),
                                                out_bytes: a["outBytes"].as_f64().unwrap_or(0.0),
                                            })
                                            .collect()
                                    })
                                })
                                .unwrap_or_default();
                                sel_data = Some(SelData { t0, t1, hosts, apps });
                                paused_dirty = true;
                            }
                        },
                    },
                    Err(std::sync::mpsc::RecvTimeoutError::Timeout) => {}
                    Err(std::sync::mpsc::RecvTimeoutError::Disconnected) => break,
                }
                // selection active -> pause live refreshes (the whole view
                // freezes so the selected range stays in focus; a plain click
                // on the plot clears the selection and unpauses)
                if sel_data.is_some() {
                    // paused: republish the frozen snapshot only when the
                    // selection changed or a detail fetch is pending
                    if paused_dirty || app_detail_req.is_some() {
                        let prev = snap.lock().unwrap().clone();
                        let mut p2 = (*prev).clone();
                        p2.sel = sel_data.clone();
                        if let Some((aid, f, t)) = app_detail_req {
                            p2.app_detail = fetch_json(&format!(
                                "{}/api/appinfo?app_id={}&from={}&to={}",
                                API, aid, f, t
                            ))
                            .ok()
                            .map(|d| parse_appinfo(&d, aid, f, t));
                            app_detail_req = None;
                        }
                        *snap.lock().unwrap() = Arc::new(p2);
                        paused_dirty = false;
                        if let Some(ctx) = ui_ctx.lock().unwrap().as_ref() {
                            ctx.request_repaint();
                        }
                    }
                    continue;
                }
                let fetch_t0 = std::time::Instant::now();
                let prev_snap = snap.lock().unwrap().clone();
                let mut fresh = Some(fetch_snapshot(
                    &tab,
                    &sel,
                    period,
                    if prev_snap.traffic.is_some() {
                        Some(prev_snap.as_ref())
                    } else {
                        None
                    },
                    tick,
                ));
                last_fetch_dur = fetch_t0.elapsed();
                tick = tick.wrapping_add(1);
                if let Some((aid, f, t)) = app_detail_req {
                    let detail = fetch_json(&format!(
                        "{}/api/appinfo?app_id={}&from={}&to={}",
                        API, aid, f, t
                    ))
                    .ok()
                    .map(|d| parse_appinfo(&d, aid, f, t));
                    if let Some(fr) = fresh.as_mut() {
                        fr.app_detail = detail;
                    }
                    app_detail_req = None;
                }
                if let Some(fr) = fresh.as_mut() {
                    fr.sel = sel_data.clone();
                    let mut want: Vec<i64> = fr.apps.iter().map(|a| a.id).collect();
                    want.extend(fr.usage_per_app.iter().map(|r| r.0));
                    want.sort_unstable();
                    want.dedup();
                    let mut fetched = 0;
                    for id in want {
                        if fetched >= 2 {
                            break;
                        }
                        if icon_cache.contains_key(&id) || icon_failed.contains(&id) {
                            continue;
                        }
                        match ureq::get(&format!("{}/api/icon?app_id={}", API, id))
                            .timeout(Duration::from_secs(4))
                            .call()
                        {
                            Ok(resp) => {
                                if resp.status() == 200 {
                                    let mut bytes = Vec::new();
                                    use std::io::Read;
                                    if resp
                                        .into_reader()
                                        .take(200_000)
                                        .read_to_end(&mut bytes)
                                        .is_ok()
                                        && !bytes.is_empty()
                                    {
                                        icon_cache.insert(id, bytes);
                                        fetched += 1;
                                    }
                                } else {
                                    icon_failed.insert(id);
                                }
                            }
                            Err(_) => {
                                icon_failed.insert(id);
                            }
                        }
                    }
                    for row in fr.apps.iter_mut() {
                        row.icon = icon_cache.get(&row.id).cloned();
                    }
                    fr.icons = icon_cache.clone();
                }
                if let Some(fr) = fresh {
                    *snap.lock().unwrap() = Arc::new(fr);
                }
                if let Some(ctx) = ui_ctx.lock().unwrap().as_ref() {
                    ctx.request_repaint();
                }
            }
        });
    }

    let options = eframe::NativeOptions {
        viewport: egui::ViewportBuilder::default()
            .with_inner_size([1240.0, 820.0])
            .with_min_inner_size([940.0, 640.0])
            .with_title("Packetly")
            .with_icon(app_icon()),
        ..Default::default()
    };

    let snap_for_app = snap.clone();
    let flags_for_app = flags.clone();
    let ctx_holder = ui_ctx.clone();
    let zoom0 = zoom;
    eframe::run_native(
        "Packetly",
        options,
        Box::new(move |cc| {
            install_fonts(&cc.egui_ctx);
            cc.egui_ctx.set_pixels_per_point(zoom0);
            *ctx_holder.lock().unwrap() = Some(cc.egui_ctx.clone());
            Ok(Box::new(PacketlyApp {
                tab: Tab::Graph,
                sel: RangeSel::Preset(Range::M5),
                cal_view: (2026, 1),
                cal_anchor: None,
                show_custom: false,
                period: UsagePeriod::Today,
                zoom: zoom0,
                theme_kind,
                snap: snap_for_app,
                tx,
                cache: Mutex::new(ChartCache {
                    version: 0,
                    pivot: None,
                    rendered: None,
                    mini: None,
                }),
                icon_tex: HashMap::new(),
                quota_app: String::new(),
                quota_period: "daily".to_string(),
                quota_value: String::new(),
                quota_unit: 1048576,
                auto_scale,
                scale_cap,
                show_scale: false,
                sel_drag: None,
                traffic_sort: SortKey::Total,
                sel_sort: SortKey::Total,
                detail_tab: 0,
                detail_sort: SortKey::Down,
                settings_open: false,
                start_minimized,
                flags: flags_for_app,
                anim_x1: 0.0,
                anim_y_top: 0.0,
                anim_k: 1.0,
                anim_last: None,
                anim_sel_key: String::new(),
                snap_x1_seen: 0.0,
                snap_x1_at: None,
            }))
        }),
    )
}

fn install_fonts(ctx: &egui::Context) {
    let mut fonts = egui::FontDefinitions::default();
    for (key, file) in [
        ("segoe_ui", "C:/Windows/Fonts/segoeui.ttf"),
        ("segoe_sym", "C:/Windows/Fonts/seguisym.ttf"),
    ] {
        match std::fs::read(file) {
            Ok(bytes) => {
                fonts.font_data.insert(key.to_string(), egui::FontData::from_owned(bytes));
            }
            Err(_) => continue,
        }
    }
    if fonts.font_data.contains_key("segoe_ui") {
        let fam = fonts.families.entry(egui::FontFamily::Proportional).or_default();
        fam.insert(0, "segoe_ui".into());
    }
    if fonts.font_data.contains_key("segoe_sym") {
        fonts
            .families
            .entry(egui::FontFamily::Proportional)
            .or_default()
            .push("segoe_sym".into());
        fonts
            .families
            .entry(egui::FontFamily::Monospace)
            .or_default()
            .push("segoe_sym".into());
    }
    ctx.set_fonts(fonts);
}

impl PacketlyApp {
    /// App details: click any app row (Traffic, Usage, selection popup) to open
    /// bandwidth / name / path / version / publisher plus the app's hosts and
    /// alerts for the selected period.
    fn app_detail_popup(&mut self, ctx: &egui::Context) {
        let detail = {
            let snap = self.snap.lock().unwrap().clone();
            snap.app_detail.clone()
        };
        let Some(d) = detail else { return };
        let t = self.theme();
        egui::Area::new(egui::Id::new("app-detail"))
            .order(egui::Order::Foreground)
            .anchor(egui::Align2::LEFT_BOTTOM, [14.0, -14.0])
            .show(ctx, |ui| {
                egui::Frame::popup(ui.style())
                    .fill(t.card)
                    .stroke(egui::Stroke::new(1.0, t.border))
                    .show(ui, |ui| {
                        ui.set_min_width(470.0);
                        ui.set_max_width(470.0);

                        // header
                        ui.horizontal(|ui| {
                            if let Some(tex) =
                                self.app_icon_opt(ctx, d.app_id, &d.name)
                            {
                                ui.add(
                                    egui::Image::from_texture(&tex)
                                        .max_size(egui::vec2(24.0, 24.0)),
                                );
                            }
                            ui.strong(egui::RichText::new(&d.name).size(14.0).color(t.text));
                            ui.with_layout(
                                egui::Layout::right_to_left(egui::Align::Center),
                                |ui| {
                                    if ui.small_button("✕").clicked() {
                                        let _ = self.tx.send(UiMsg::AppDetail(None));
                                    }
                                },
                            );
                        });

                        // tabs
                        ui.horizontal(|ui| {
                            let tabs: [(String, usize); 3] = [
                                ("Info".to_string(), 0),
                                (format!("Hosts ({})", d.hosts.len()), 1),
                                (format!("Alerts ({})", d.alert_count), 2),
                            ];
                            for (label, idx) in &tabs {
                                if ui
                                    .selectable_label(self.detail_tab == *idx, label.as_str())
                                    .clicked()
                                {
                                    self.detail_tab = *idx;
                                }
                            }
                        });
                        ui.separator();

                        match self.detail_tab {
                            0 => {
                                // Info
                                let row = |ui: &mut egui::Ui, k: &str, v: egui::RichText| {
                                    ui.horizontal(|ui| {
                                        ui.set_min_width(430.0);
                                        ui.weak(
                                            egui::RichText::new(k).size(11.5).color(t.dim),
                                        );
                                        ui.with_layout(
                                            egui::Layout::right_to_left(egui::Align::Center),
                                            |ui| {
                                                ui.add(
                                                    egui::Label::new(v.size(11.5))
                                                        .truncate(),
                                                );
                                            },
                                        );
                                    });
                                };
                                ui.add_space(2.0);
                                row(
                                    ui,
                                    "Bandwidth:",
                                    egui::RichText::new(format!(
                                        "↓ {}   ↑ {}",
                                        fmt_b(d.in_bytes),
                                        fmt_b(d.out_bytes)
                                    ))
                                    .color(t.text),
                                );
                                row(
                                    ui,
                                    "Name:",
                                    egui::RichText::new(&d.name).color(t.text),
                                );
                                row(
                                    ui,
                                    "Path:",
                                    egui::RichText::new(
                                        d.path.as_deref().unwrap_or("—"),
                                    )
                                    .color(t.text),
                                );
                                row(
                                    ui,
                                    "Version:",
                                    egui::RichText::new(
                                        d.version.as_deref().unwrap_or("—"),
                                    )
                                    .color(t.text),
                                );
                                row(
                                    ui,
                                    "Publisher:",
                                    egui::RichText::new(
                                        d.publisher.as_deref().unwrap_or("—"),
                                    )
                                    .color(t.text),
                                );
                                ui.add_space(4.0);
                            }
                            1 => {
                                // Hosts
                                let mut hosts = d.hosts.clone();
                                match self.detail_sort {
                                    SortKey::Name => hosts.sort_by(|a, b| {
                                        a.label.to_lowercase()
                                            .cmp(&b.label.to_lowercase())
                                    }),
                                    SortKey::Up => hosts.sort_by(|a, b| {
                                        b.out_bytes
                                            .partial_cmp(&a.out_bytes)
                                            .unwrap_or(std::cmp::Ordering::Equal)
                                    }),
                                    SortKey::Down => hosts.sort_by(|a, b| {
                                        b.in_bytes
                                            .partial_cmp(&a.in_bytes)
                                            .unwrap_or(std::cmp::Ordering::Equal)
                                    }),
                                    _ => hosts.sort_by(|a, b| {
                                        (b.in_bytes + b.out_bytes)
                                            .partial_cmp(&(a.in_bytes + a.out_bytes))
                                            .unwrap_or(std::cmp::Ordering::Equal)
                                    }),
                                }
                                ui.horizontal(|ui| {
                                    ui.weak(
                                        egui::RichText::new("Sort")
                                            .size(10.5)
                                            .color(t.dim),
                                    );
                                    egui::ComboBox::from_id_source("detail_host_sort")
                                        .selected_text(self.detail_sort.label())
                                        .width(86.0)
                                        .show_ui(ui, |ui| {
                                            for k in SortKey::detail_keys() {
                                                ui.selectable_value(
                                                    &mut self.detail_sort,
                                                    k,
                                                    k.label(),
                                                );
                                            }
                                        });
                                });
                                egui::ScrollArea::vertical()
                                    .max_height(210.0)
                                    .show(ui, |ui| {
                                        if hosts.is_empty() {
                                            ui.weak("no hosts in this period");
                                        }
                                        for h in &hosts {
                                            ui.horizontal(|ui| {
                                                ui.set_min_width(430.0);
                                                if let Some(rg) = h.region.as_deref() {
                                                    flag_ui(ui, rg);
                                                }
                                                ui.add(
                                                    egui::Label::new(
                                                        egui::RichText::new(&h.label)
                                                            .size(11.5)
                                                            .color(t.text),
                                                    )
                                                    .truncate(),
                                                );
                                                ui.with_layout(
                                                    egui::Layout::right_to_left(
                                                        egui::Align::Center,
                                                    ),
                                                    |ui| {
                                                        ui.weak(
                                                            egui::RichText::new(format!(
                                                                "↑ {}",
                                                                fmt_b(h.out_bytes)
                                                            ))
                                                            .color(t.out_c),
                                                        );
                                                        ui.weak(
                                                            egui::RichText::new(format!(
                                                                "↓ {}",
                                                                fmt_b(h.in_bytes)
                                                            ))
                                                            .color(t.in_c),
                                                        );
                                                    },
                                                );
                                            });
                                        }
                                    });
                                ui.add_space(2.0);
                            }
                            _ => {
                                // Alerts for this app
                                let snap = self.snap.lock().unwrap().clone();
                                let mine: Vec<&EventRow> = snap
                                    .events
                                    .iter()
                                    .filter(|e| {
                                        e.app_name.as_deref() == Some(d.name.as_str())
                                    })
                                    .collect();
                                egui::ScrollArea::vertical()
                                    .max_height(210.0)
                                    .show(ui, |ui| {
                                        if mine.is_empty() {
                                            ui.weak("no alerts for this app");
                                        }
                                        for e in mine {
                                            ui.horizontal(|ui| {
                                                ui.set_min_width(430.0);
                                                ui.weak(
                                                    egui::RichText::new(rel_time(e.ts))
                                                        .size(10.5)
                                                        .color(t.dim),
                                                );
                                                ui.add(
                                                    egui::Label::new(
                                                        egui::RichText::new(&e.data)
                                                            .size(11.0)
                                                            .color(t.text),
                                                    )
                                                    .truncate(),
                                                );
                                            });
                                        }
                                    });
                                ui.add_space(2.0);
                            }
                        }
                    });
            });
    }
}
