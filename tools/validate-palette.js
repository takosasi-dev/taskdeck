#!/usr/bin/env node
// 色の検証（CLAUDE.md 1.5 / UI 設計書 2.5 / NFR 8章 #19）。依存パッケージなし。Node 18 以降。
//
//   node tools/validate-palette.js "#0067C0,#CA5010,..." --mode light   プロジェクト色（隣接ΔE・彩度・地とのコントラスト）
//   node tools/validate-palette.js "#..." --mode dark
//   node tools/validate-palette.js --contrast                            テーマ辞書の文字色×地のコントラスト
//   node tools/validate-palette.js --accents "#0067C0/#4CC2FF,..."       固定アクセント色（ライト/ダークの組）
//   node tools/validate-palette.js --self-check                          尺度の校正（設計書の数値の再現）
//
// 終了コード: 合格 0 ／ 不合格 1（条件付き合格も 0。理由は出力に書く）。
//
// 尺度: ΔE は OKLab 空間の距離×100。色覚シミュレーションは Machado, Oliveira, Fernandes (2009) の
// 行列（severity 1.0）を線形 RGB に掛ける。UI 設計書 2.5 の数値（ライト隣接最小 21.2 / 2型 6.2、
// 却下した #A85C00 と #C2416B の 14.3、彩度 #0F7B6C=0.092・#4A5568=0.034）が再現できることを
// `--self-check` で確かめる（1つでもずれたら終了コード 1）。

'use strict';

const fs = require('fs');
const path = require('path');

// ---------------------------------------------------------------- 基準値

const THRESHOLDS = {
    deltaNormal: 15, // 正常色覚の隣接ΔE
    deltaCvd: 8, // 色覚特性の隣接ΔE（6〜8 は条件付き合格）
    deltaCvdWarn: 6,
    chroma: 0.1, // OKLCh の C（下回ると灰色に見える）
    contrast: 3, // 地とのコントラスト（面の塗り分け）
    textContrast: 4.5, // 文字のコントラスト（NFR #19）
    largeTextContrast: 3, // 24px 以上の見出し
};

const BACKGROUNDS = { light: '#FFFFFF', dark: '#272727' };

// Machado ら 2009、severity 1.0（線形 RGB に掛ける）
const CVD_MATRICES = {
    '1型（P型）': [
        [0.152286, 1.052583, -0.204868],
        [0.114503, 0.786281, 0.099216],
        [-0.003882, -0.048116, 1.051998],
    ],
    '2型（D型）': [
        [0.367322, 0.860646, -0.227968],
        [0.280085, 0.672501, 0.047413],
        [-0.011820, 0.042940, 0.968881],
    ],
    '3型（T型）': [
        [1.255528, -0.076749, -0.178779],
        [-0.078411, 0.930809, 0.147602],
        [0.004733, 0.691367, 0.303900],
    ],
};

// ---------------------------------------------------------------- 色の計算

function parseHex(hex) {
    const text = String(hex).trim().replace(/^#/, '');
    const body = text.length === 8 ? text.slice(2) : text; // #AARRGGBB（XAML）も受ける
    if (!/^[0-9a-fA-F]{6}$/.test(body)) {
        throw new Error(`色として読めません: ${hex}`);
    }
    return [
        parseInt(body.slice(0, 2), 16) / 255,
        parseInt(body.slice(2, 4), 16) / 255,
        parseInt(body.slice(4, 6), 16) / 255,
    ];
}

function formatHex(rgb) {
    const to = (v) => Math.round(Math.min(1, Math.max(0, v)) * 255).toString(16).padStart(2, '0').toUpperCase();
    return `#${to(rgb[0])}${to(rgb[1])}${to(rgb[2])}`;
}

const toLinear = (c) => (c <= 0.04045 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4));
const toSrgb = (c) => (c <= 0.0031308 ? c * 12.92 : 1.055 * Math.pow(c, 1 / 2.4) - 0.055);

const linearRgb = (rgb) => rgb.map(toLinear);

function oklab(rgb) {
    const [r, g, b] = linearRgb(rgb);
    const l = Math.cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
    const m = Math.cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
    const s = Math.cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
    return {
        L: 0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
        a: 1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
        b: 0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s,
    };
}

function oklabToRgb(lab) {
    const l = Math.pow(lab.L + 0.3963377774 * lab.a + 0.2158037573 * lab.b, 3);
    const m = Math.pow(lab.L - 0.1055613458 * lab.a - 0.0638541728 * lab.b, 3);
    const s = Math.pow(lab.L - 0.0894841775 * lab.a - 1.2914855480 * lab.b, 3);
    const lin = [
        4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
        -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
        -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s,
    ];
    return lin.map((c) => Math.min(1, Math.max(0, toSrgb(Math.min(1, Math.max(0, c))))));
}

const chroma = (rgb) => {
    const c = oklab(rgb);
    return Math.hypot(c.a, c.b);
};

const hue = (rgb) => {
    const c = oklab(rgb);
    const h = (Math.atan2(c.b, c.a) * 180) / Math.PI;
    return h < 0 ? h + 360 : h;
};

/** OKLab 距離×100。UI 設計書 2.5 の数値はこの尺度。 */
function deltaE(rgbA, rgbB) {
    const x = oklab(rgbA);
    const y = oklab(rgbB);
    return Math.hypot(x.L - y.L, x.a - y.a, x.b - y.b) * 100;
}

/** 色覚特性のシミュレーション（線形 RGB に行列を掛けて戻す）。 */
function simulate(rgb, matrix) {
    const [r, g, b] = linearRgb(rgb);
    const out = matrix.map((row) => row[0] * r + row[1] * g + row[2] * b);
    return out.map((c) => Math.min(1, Math.max(0, toSrgb(Math.min(1, Math.max(0, c))))));
}

function relativeLuminance(rgb) {
    const [r, g, b] = linearRgb(rgb);
    return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}

function contrastRatio(rgbA, rgbB) {
    const a = relativeLuminance(rgbA);
    const b = relativeLuminance(rgbB);
    const [hi, lo] = a >= b ? [a, b] : [b, a];
    return (hi + 0.05) / (lo + 0.05);
}

// ---------------------------------------------------------------- 表の整形

const width = (text) => [...String(text)].reduce((n, ch) => n + (/[　-鿿！-｠]/.test(ch) ? 2 : 1), 0);

function pad(text, size, align = 'left') {
    const space = ' '.repeat(Math.max(0, size - width(text)));
    return align === 'right' ? space + text : text + space;
}

function table(headers, rows, aligns = []) {
    const sizes = headers.map((h, i) => Math.max(width(h), ...rows.map((r) => width(r[i] ?? ''))));
    const line = (cells) => '| ' + cells.map((c, i) => pad(c ?? '', sizes[i], aligns[i])).join(' | ') + ' |';
    const out = [line(headers), '|' + sizes.map((s) => '-'.repeat(s + 2)).join('|') + '|'];
    rows.forEach((r) => out.push(line(r)));
    return out.join('\n');
}

const mark = (ok, warn = false) => (ok ? (warn ? '△' : '○') : '×');
const round = (value, digits = 1) => value.toFixed(digits);

// ---------------------------------------------------------------- パレットの検証

function validatePalette(hexes, mode) {
    const background = parseHex(BACKGROUNDS[mode]);
    const colors = hexes.map((hex) => ({ hex: formatHex(parseHex(hex)), rgb: parseHex(hex) }));
    let failed = 0;
    let warned = 0;

    console.log(`\n== プロジェクト色（${mode} / 地 ${BACKGROUNDS[mode]}）==\n`);

    const colorRows = colors.map((c, i) => {
        const lab = oklab(c.rgb);
        const ch = chroma(c.rgb);
        const cr = contrastRatio(c.rgb, background);
        const chOk = ch >= THRESHOLDS.chroma;
        const crOk = cr >= THRESHOLDS.contrast;
        if (!chOk || !crOk) {
            failed++;
        }
        return [
            String(i + 1),
            c.hex,
            round(lab.L, 3),
            round(ch, 3),
            round(hue(c.rgb), 0),
            round(cr, 2),
            `${mark(chOk)}${mark(crOk)}`,
        ];
    });
    console.log(table(
        ['#', '色', 'L', 'C(彩度)', 'H', '地との比', '彩度/比'],
        colorRows,
        ['right', 'left', 'right', 'right', 'right', 'right', 'left']));
    console.log(`  基準: 彩度 ${THRESHOLDS.chroma} 以上 / 地とのコントラスト ${THRESHOLDS.contrast}:1 以上`);

    console.log('\n-- 隣り合う色のΔE --\n');
    const names = ['正常色覚', ...Object.keys(CVD_MATRICES)];
    const pairRows = [];
    for (let i = 0; i + 1 < colors.length; i++) {
        const a = colors[i];
        const b = colors[i + 1];
        const values = [deltaE(a.rgb, b.rgb)];
        for (const matrix of Object.values(CVD_MATRICES)) {
            values.push(deltaE(simulate(a.rgb, matrix), simulate(b.rgb, matrix)));
        }
        const normalOk = values[0] >= THRESHOLDS.deltaNormal;
        const cvdMin = Math.min(...values.slice(1));
        const cvdOk = cvdMin >= THRESHOLDS.deltaCvd;
        const cvdWarn = !cvdOk && cvdMin >= THRESHOLDS.deltaCvdWarn;
        if (!normalOk || (!cvdOk && !cvdWarn)) {
            failed++;
        } else if (cvdWarn) {
            warned++;
        }
        pairRows.push([
            `${i + 1}-${i + 2}`,
            `${a.hex} / ${b.hex}`,
            ...values.map((v) => round(v)),
            `${mark(normalOk)}${mark(cvdOk || cvdWarn, cvdWarn)}`, // 6〜8 の帯は × ではなく △（条件付き）
        ]);
    }
    console.log(table(
        ['対', '色', ...names, '正常/色覚'],
        pairRows,
        ['left', 'left', 'right', 'right', 'right', 'right', 'left']));
    console.log(`  基準: 正常色覚 ${THRESHOLDS.deltaNormal} 以上 / 色覚特性 ${THRESHOLDS.deltaCvd} 以上`
        + `（${THRESHOLDS.deltaCvdWarn}〜${THRESHOLDS.deltaCvd} は凡例＋直接ラベル併用が条件の△）`);

    if (failed > 0) {
        console.log(`\n結果: 不合格（${failed} 件）`);
    } else if (warned > 0) {
        console.log(`\n結果: 条件付き合格（△ ${warned} 件 — 凡例と直接ラベルを必ず併用する）`);
    } else {
        console.log('\n結果: 合格');
    }
    return failed === 0;
}

// ---------------------------------------------------------------- コントラストの検証

const THEME_DIR = path.join(__dirname, '..', 'src', 'TaskDeck.App', 'Resources', 'Themes');

/** Light.xaml / Dark.xaml の <Color x:Key="...">#AARRGGBB</Color> を読む。 */
function readThemeColors(file) {
    const xaml = fs.readFileSync(file, 'utf8');
    const colors = {};
    const pattern = /<Color\s+x:Key="([^"]+)"\s*>\s*(#[0-9a-fA-F]{6,8})\s*<\/Color>/g;
    let match;
    while ((match = pattern.exec(xaml)) !== null) {
        colors[match[1]] = match[2];
    }
    return colors;
}

// 文字色 × 地（UI 設計書 2.1〜2.4 の組み合わせのうち実際に重ねるもの）
const FOREGROUNDS = [
    { key: 'TextPrimaryColor', label: 'Text.Primary' },
    { key: 'TextSecondaryColor', label: 'Text.Secondary' },
    { key: 'TextTertiaryColor', label: 'Text.Tertiary' },
    { key: 'StateOverdueColor', label: 'State.Overdue' },
    { key: 'PriorityUrgentColor', label: '優先度 緊急' },
    { key: 'PriorityHighColor', label: '優先度 高' },
    { key: 'PriorityMediumColor', label: '優先度 中' },
    { key: 'PriorityLowColor', label: '優先度 低' },
    { key: 'AccentDefaultColor', label: 'Accent.Default' },
];

// 文字が乗る面（行のホバーと設定カードも文字を載せるので含める）
const SURFACES = [
    { key: 'SurfaceWindowColor', label: 'Window' },
    { key: 'SurfaceContentColor', label: 'Content' },
    { key: 'SurfaceSubtleColor', label: 'Subtle' },
    { key: 'SurfaceSelectedColor', label: 'Selected' },
    { key: 'SurfaceHoverColor', label: 'Hover' },
    { key: 'SettingsCardColor', label: 'SettingsCard' },
];

// 地が決まっている組（チップ・トースト・ボタン）。4つ目は基準（省略時は文字の 4.5）
const FIXED_PAIRS = [
    ['StateOverdueColor', 'StateOverdueBgColor', '期限切れチップ'],
    ['PastWarnForegroundColor', 'PastWarnBackgroundColor', '過去日の警告'],
    ['ToastForegroundColor', 'ToastBackgroundColor', 'トーストの文字'],
    ['ToastActionColor', 'ToastBackgroundColor', 'トーストの操作'],
    ['BulkDangerColor', 'ToastBackgroundColor', '一括の破壊的操作'],
    ['TextOnAccentColor', 'AccentDefaultColor', 'アクセント上の文字'],
    ['TextOnAccentColor', 'AccentHoverColor', 'アクセント上の文字（ホバー）'],
    ['TextOnDangerColor', 'DangerColor', '破壊的ボタンの文字'],
    ['TextPrimaryColor', 'NavSelectedColor', '選択中のナビ項目の文字'],
    ['TextSecondaryColor', 'NavSelectedColor', '選択中のナビ項目の件数'],
    // 文字ではない部品（アイコン・縦バー・チェックの枠）は 3:1（WCAG 1.4.11）
    ['AccentDefaultColor', 'NavSelectedColor', '選択中のナビのアイコンと縦バー', 3],
    ['StrokeControlStrongColor', 'SurfaceContentColor', '未チェックの枠・入力欄の下辺', 3],
    ['StrokeControlStrongColor', 'SurfaceSelectedColor', '未チェックの枠（選択行の上）', 3],
];

function validateContrast() {
    let failed = 0;
    for (const mode of ['light', 'dark']) {
        const file = path.join(THEME_DIR, mode === 'light' ? 'Light.xaml' : 'Dark.xaml');
        const colors = readThemeColors(file);
        console.log(`\n== 文字のコントラスト（${mode} / ${path.basename(file)}）==\n`);

        const rows = [];
        for (const fg of FOREGROUNDS) {
            const row = [fg.label];
            for (const bg of SURFACES) {
                if (!colors[fg.key] || !colors[bg.key]) {
                    row.push('—');
                    continue;
                }
                const ratio = contrastRatio(parseHex(colors[fg.key]), parseHex(colors[bg.key]));
                const ok = ratio >= THRESHOLDS.textContrast;
                if (!ok) {
                    failed++;
                }
                row.push(`${round(ratio, 2)} ${mark(ok)}`);
            }
            rows.push(row);
        }
        console.log(table(['文字', ...SURFACES.map((s) => s.label)], rows, ['left', ...SURFACES.map(() => 'right')]));

        const fixedRows = [];
        for (const [fgKey, bgKey, label, threshold = THRESHOLDS.textContrast] of FIXED_PAIRS) {
            if (!colors[fgKey] || !colors[bgKey]) {
                console.log(`  （${label}: ${fgKey} か ${bgKey} が辞書にありません）`);
                failed++;
                continue;
            }
            const ratio = contrastRatio(parseHex(colors[fgKey]), parseHex(colors[bgKey]));
            const ok = ratio >= threshold;
            if (!ok) {
                failed++;
            }
            fixedRows.push([label, `${colors[fgKey]} / ${colors[bgKey]}`, `${threshold}:1`, `${round(ratio, 2)} ${mark(ok)}`]);
        }
        console.log('');
        console.log(table(['地が決まっている組', '色 / 地', '基準', '比'], fixedRows, ['left', 'left', 'right', 'right']));
        console.log(`  基準: 文字は ${THRESHOLDS.textContrast}:1 以上（見出し 22px 以上は ${THRESHOLDS.largeTextContrast}:1 で可。`
            + '見出しに使うのは Text.Primary だけ）。アイコン・枠などの部品は 3:1 以上');
    }
    console.log(failed === 0 ? '\n結果: 合格' : `\n結果: 不合格（${failed} 件）`);
    return failed === 0;
}

// ---------------------------------------------------------------- 固定アクセント色の検証

/**
 * 設定の「アクセントカラー」で選べる固定色（ライト/ダークの組）。ThemeService.AccentChoices と同じ値を渡す。
 * アクセントは文字（リンク・選択中のアイコン）にも使うので、テーマ辞書の全部の面に対して 4.5:1、
 * アクセント地の上の文字（Text.OnAccent）も 4.5:1 を求める。役割（色相）がライトとダークで同じかも表に出す。
 */
function validateAccents(pairsText) {
    const pairs = pairsText.split(',').map((s) => s.trim()).filter(Boolean).map((pair) => {
        const [light, dark] = pair.split('/').map((s) => s.trim());
        if (!light || !dark) {
            throw new Error(`「ライト/ダーク」の形で渡してください: ${pair}`);
        }
        return { light: formatHex(parseHex(light)), dark: formatHex(parseHex(dark)) };
    });
    const themes = {
        light: readThemeColors(path.join(THEME_DIR, 'Light.xaml')),
        dark: readThemeColors(path.join(THEME_DIR, 'Dark.xaml')),
    };
    let failed = 0;
    const rows = pairs.map((p) => {
        const cells = [`${p.light} / ${p.dark}`];
        const marks = [];
        for (const mode of ['light', 'dark']) {
            const colors = themes[mode];
            const rgb = parseHex(p[mode]);
            const minSurface = Math.min(...SURFACES.map((s) => contrastRatio(rgb, parseHex(colors[s.key]))));
            const onAccent = contrastRatio(parseHex(colors.TextOnAccentColor), rgb);
            const ok = minSurface >= THRESHOLDS.textContrast && onAccent >= THRESHOLDS.textContrast;
            if (!ok) {
                failed++;
            }
            cells.push(round(minSurface, 2), round(onAccent, 2));
            marks.push(mark(ok));
        }
        const hueGap = Math.abs(((hue(parseHex(p.light)) - hue(parseHex(p.dark)) + 540) % 360) - 180);
        cells.push(round(hueGap, 0), marks.join(''));
        return cells;
    });
    console.log('\n== 固定アクセント色（ライト / ダーク）==\n');
    console.log(table(
        ['色（ライト / ダーク）', 'ライト 面との最小比', 'ライト 上の文字', 'ダーク 面との最小比', 'ダーク 上の文字', '色相差', 'ライト/ダーク'],
        rows,
        ['left', 'right', 'right', 'right', 'right', 'right', 'left']));
    console.log(`  基準: 面（${SURFACES.map((s) => s.label).join('・')}）との比と、アクセント地の上の文字（Text.OnAccent）の比が`
        + ` どちらも ${THRESHOLDS.textContrast}:1 以上。色相差は参考（役割が同じか）`);
    console.log(failed === 0 ? '\n結果: 合格' : `\n結果: 不合格（${failed} 件）`);
    return failed === 0;
}

// ---------------------------------------------------------------- 尺度の校正

/** UI 設計書 2.5 に載っている数値を再現できるか。 */
function selfCheck() {
    const light = ['#0067C0', '#CA5010', '#8764B8', '#0E7C5A', '#C2417A', '#8A6D00'].map(parseHex);
    const deutan = CVD_MATRICES['2型（D型）'];
    let normalMin = Infinity;
    let deutanMin = Infinity;
    for (let i = 0; i + 1 < light.length; i++) {
        normalMin = Math.min(normalMin, deltaE(light[i], light[i + 1]));
        deutanMin = Math.min(deutanMin, deltaE(simulate(light[i], deutan), simulate(light[i + 1], deutan)));
    }
    const rejected = deltaE(parseHex('#A85C00'), parseHex('#C2416B'));
    const rows = [
        ['ライト隣接ΔE の最小（正常色覚）', '21.2', round(normalMin)],
        ['ライト隣接ΔE の最小（2型色覚）', '6.2', round(deutanMin)],
        ['却下した #A85C00 / #C2416B のΔE', '14.3', round(rejected)],
        ['#0F7B6C の彩度', '0.092', round(chroma(parseHex('#0F7B6C')), 3)],
        ['#4A5568 の彩度', '0.034', round(chroma(parseHex('#4A5568')), 3)],
    ];
    // 設計書と同じ桁に丸めて一致するか（計算式や行列を変えてずれたら落とす）
    const mismatched = rows.filter((r) => r[1] !== r[2]);
    console.log('\n== 尺度の校正（UI 設計書 2.5）==\n');
    console.log(table(['項目', '設計書', 'この計算', ''], rows.map((r) => [...r, mark(r[1] === r[2])]),
        ['left', 'right', 'right', 'left']));
    console.log(mismatched.length === 0
        ? '\n結果: 設計書の数値をすべて再現（ΔE は OKLab 距離×100、Machado 2009 の行列）'
        : `\n結果: ${mismatched.length} 件が設計書と合いません`);
    return mismatched.length === 0;
}

/**
 * ライトの各色から、明度を上げたダーク候補を作る（調整の出発点を出すだけ）。
 * targetL / boostC は色ごとに変えられる（カンマ区切り。1つだけなら全色に使う）。
 */
function suggestDark(hexes, targetL, boostC) {
    console.log('\n== ダーク候補（L を上げ、彩度を保つ）==\n');
    const at = (list, i) => list[Math.min(i, list.length - 1)];
    const rows = hexes.map((hex, i) => {
        const lab = oklab(parseHex(hex));
        const L = at(targetL, i);
        const scale = at(boostC, i);
        const candidate = oklabToRgb({ L, a: lab.a * scale, b: lab.b * scale });
        return [hex, formatHex(candidate), round(L, 3), round(chroma(candidate), 3)];
    });
    console.log(table(['ライト', '候補', 'L', 'C'], rows, ['left', 'left', 'right', 'right']));
    console.log(rows.map((r) => r[1]).join(','));
    return true;
}

// ---------------------------------------------------------------- 入口

function main(argv) {
    const args = argv.slice(2);
    if (args.includes('--help') || args.includes('-h')) {
        console.log('使い方:\n'
            + '  node tools/validate-palette.js "#0067C0,#CA5010,..." --mode light|dark\n'
            + '  node tools/validate-palette.js --contrast\n'
            + '  node tools/validate-palette.js --accents "#0067C0/#4CC2FF,#8764B8/#A981E1"\n'
            + '  node tools/validate-palette.js --self-check\n'
            + '  node tools/validate-palette.js "#..." --suggest-dark [--target-l 0.72] [--boost 1.0]');
        return 0;
    }
    if (args.includes('--self-check')) {
        return selfCheck() ? 0 : 1;
    }
    if (args.includes('--contrast')) {
        return validateContrast() ? 0 : 1;
    }
    const accentsIndex = args.indexOf('--accents');
    if (accentsIndex >= 0) {
        const pairs = args[accentsIndex + 1];
        if (!pairs) {
            console.error('--accents の後に「ライト/ダーク」の組をカンマ区切りで渡してください');
            return 1;
        }
        return validateAccents(pairs) ? 0 : 1;
    }

    const hexes = (args.find((a) => !a.startsWith('--')) ?? '').split(',').map((s) => s.trim()).filter(Boolean);
    if (hexes.length === 0) {
        console.error('色を渡してください。例: node tools/validate-palette.js "#0067C0,#CA5010" --mode light');
        return 1;
    }
    if (args.includes('--suggest-dark')) {
        const values = (name, fallback) => {
            const i = args.indexOf(name);
            return i >= 0 && args[i + 1] ? args[i + 1].split(',').map(Number) : [fallback];
        };
        return suggestDark(hexes, values('--target-l', 0.72), values('--boost', 1.0)) ? 0 : 1;
    }

    const modeIndex = args.indexOf('--mode');
    const mode = modeIndex >= 0 ? args[modeIndex + 1] : 'light';
    if (!BACKGROUNDS[mode]) {
        console.error(`--mode は light か dark（受け取った値: ${mode}）`);
        return 1;
    }
    return validatePalette(hexes, mode) ? 0 : 1;
}

process.exitCode = main(process.argv);
