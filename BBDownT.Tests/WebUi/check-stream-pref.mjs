// 检查网页前端「按上次的选择自动选中流」的匹配规则(index.html 里 stream-pref:begin 到 stream-pref:end 之间的纯函数)。
// 运行：node BBDownT.Tests/WebUi/check-stream-pref.mjs   (C# 测试 WebUiLinkRulesTests 在装有 node 时会自动运行它)
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const root = join(here, '..', '..');
const html = readFileSync(join(root, 'BBDownT', 'WebUi', 'index.html'), 'utf8');
const begin = html.indexOf('// stream-pref:begin');
const end = html.indexOf('// stream-pref:end');
if (begin < 0 || end < begin) {
  console.error('index.html 里找不到 stream-pref:begin / stream-pref:end 标记');
  process.exit(2);
}
const code = html.slice(html.indexOf('\n', begin) + 1, end);
const rules = new Function(`${code}\nreturn { codecOrder, matchVideoPref, matchAudioPref, guestParse };`)();

const failures = [];
let checks = 0;
const check = (ok, message) => { checks++; if (!ok) failures.push(message); };

// 与 /parse 返回的 Videos 相同的形状和顺序：画质代码从高到低，同一画质内 AVC、HEVC、AV1
const V = (id, quality, codec) => ({ Key: `${id}:${codec}`, Id: String(id), Quality: quality, Codec: codec, Size: 1 });
const Q = { 126: '杜比视界', 125: 'HDR 真彩', 120: '4K 超清', 116: '1080P 高帧率', 112: '1080P 高码率', 80: '1080P 高清', 64: '720P 高清', 32: '480P 清晰', 16: '360P 流畅' };
const list = (...items) => items.map(([id, codec]) => V(id, Q[id], codec));
const full = list([120, 'AVC'], [120, 'HEVC'], [120, 'AV1'], [116, 'AVC'], [116, 'HEVC'], [112, 'AVC'], [112, 'HEVC'],
  [80, 'AVC'], [80, 'HEVC'], [80, 'AV1'], [64, 'AVC'], [32, 'AVC']);
const pref = (qn, codec) => ({ Qn: String(qn), Quality: Q[qn], Codec: codec });

const videoCases = [
  // [说明, 上次的选择, 这次的视频流, 视频编码优先, 应选的 Key, how]
  ['画质和编码都有：原样选中', pref(120, 'HEVC'), full, '', '120:HEVC', 'exact'],
  ['编码优先顺序不影响完全相同的流', pref(80, 'AV1'), full, 'avc,hevc,av1', '80:AV1', 'exact'],
  ['同画质没有上次的编码：按编码优先换一种（HEVC 优先）', pref(120, 'AV1'), list([120, 'AVC'], [120, 'HEVC'], [80, 'AV1']), 'hevc,avc,av1', '120:HEVC', 'codec'],
  ['同画质没有上次的编码：按编码优先换一种（AVC 优先）', pref(120, 'AV1'), list([120, 'AVC'], [120, 'HEVC'], [80, 'AV1']), 'avc,hevc,av1', '120:AVC', 'codec'],
  ['同画质没有上次的编码、编码优先为自动：AV1 先换 HEVC，不换成体积最大的 AVC', pref(120, 'AV1'), list([120, 'AVC'], [120, 'HEVC']), '', '120:HEVC', 'codec'],
  ['同画质没有上次的编码、编码优先为自动：HEVC 先换 AV1', pref(120, 'HEVC'), list([120, 'AVC'], [120, 'AV1']), '', '120:AV1', 'codec'],
  ['同画质没有上次的编码、编码优先为自动：只有 AVC 时换 AVC', pref(120, 'HEVC'), list([120, 'AVC'], [80, 'HEVC']), '', '120:AVC', 'codec'],
  ['同画质没有上次的编码、编码优先为自动：AVC 先换 HEVC', pref(80, 'AVC'), list([80, 'AV1'], [80, 'HEVC']), '', '80:HEVC', 'codec'],
  ['没有这个画质、编码优先为自动：更低一档里同样按接近的编码换', pref(120, 'AV1'), list([116, 'AVC'], [116, 'HEVC'], [80, 'AV1']), '', '116:HEVC', 'lower'],
  ['没有这个画质：取更低、最接近的一档，先找上次的编码', pref(120, 'HEVC'), full.filter((v) => v.Id !== '120'), 'avc,hevc,av1', '116:HEVC', 'lower'],
  ['画质优先于编码：最接近的一档没有上次的编码时按编码优先换', pref(120, 'HEVC'), list([116, 'AVC'], [112, 'HEVC'], [80, 'HEVC']), 'hevc,avc,av1', '116:AVC', 'lower'],
  ['跳过更高的档位，只往下找', pref(112, 'AVC'), list([120, 'AVC'], [116, 'AVC'], [80, 'AVC'], [64, 'AVC']), '', '80:AVC', 'lower'],
  ['杜比视界没有时取 HDR', pref(126, 'HEVC'), list([125, 'HEVC'], [120, 'HEVC'], [120, 'AVC']), '', '125:HEVC', 'lower'],
  ['只有更高的画质：不自动选择', pref(32, 'AVC'), list([80, 'AVC'], [64, 'AVC']), '', null, null],
  ['没有视频流：不自动选择', pref(80, 'AVC'), [], '', null, null],
  ['保存的画质代码无效：不自动选择', { Qn: '4K', Codec: 'HEVC' }, full, '', null, null],
];
for (const [name, p, videos, enc, key, how] of videoCases) {
  const m = rules.matchVideoPref(p, videos, rules.codecOrder(enc));
  const actual = m ? `${m.video.Key}/${m.how}` : 'null';
  const expected = key ? `${key}/${how}` : 'null';
  check(actual === expected, `matchVideoPref：${name}：得到 ${actual}，应为 ${expected}`);
}

const A = (id, label) => ({ Id: String(id), Label: label, Codec: 'M4A', Kind: id === 30250 ? 'dolby' : id === 30251 ? 'hires' : 'normal' });
const audioCases = [
  ['ID 相同：原样选中', { Id: '30280', Label: '192K' }, [A(30251, 'Hi-Res无损'), A(30280, '192K'), A(30232, '132K')], '30280', 'exact'],
  ['杜比全景声在时原样选中', { Id: '30250', Label: '杜比全景声' }, [A(30250, '杜比全景声'), A(30280, '192K')], '30250', 'exact'],
  ['192K 没有：取 132K', { Id: '30280', Label: '192K' }, [A(30216, '64K'), A(30232, '132K')], '30232', 'lower'],
  ['64K 没有、只有更高的：不自动选择', { Id: '30216', Label: '64K' }, [A(30280, '192K'), A(30232, '132K')], null, null],
  ['Hi-Res 没有：不自动选择（按默认音频）', { Id: '30251', Label: 'Hi-Res无损' }, [A(30280, '192K')], null, null],
];
for (const [name, p, audios, id, how] of audioCases) {
  const m = rules.matchAudioPref(p, audios);
  const actual = m ? `${m.audio.Id}/${m.how}` : 'null';
  const expected = id ? `${id}/${how}` : 'null';
  check(actual === expected, `matchAudioPref：${name}：得到 ${actual}，应为 ${expected}`);
}

const account = (web, authenticated) => ({ WebLoggedIn: web, ApiAuthenticated: authenticated });
const guestCases = [
  ['WEB 已登录', { Api: 'WEB', Account: account(true, true), Hints: [] }, false],
  ['WEB 未登录', { Api: 'WEB', Account: account(false, false), Hints: [{ Code: 'not-logged-in' }] }, true],
  ['WEB 登录已失效', { Api: 'WEB', Account: account(false, false), Hints: [{ Code: 'cookie-invalid' }] }, true],
  ['WEB 没能查到登录状态', { Api: 'WEB', Account: account(false, false), Hints: [{ Code: 'account-unknown' }] }, false],
  ['APP 没有登录凭证', { Api: 'APP', Account: account(true, false), Hints: [] }, true],
  ['TV 有登录凭证', { Api: 'TV', Account: account(false, true), Hints: [] }, false],
  ['国际版', { Api: 'INTL', Account: account(false, true), Hints: [] }, false],
];
for (const [name, r, expected] of guestCases) {
  check(rules.guestParse(r) === expected, `guestParse：${name} 应为 ${expected}`);
}

check(JSON.stringify(rules.codecOrder('hevc, avc,av1')) === '["HEVC","AVC","AV1"]', 'codecOrder 应转成大写的编码列表');
check(JSON.stringify(rules.codecOrder('')) === '[]', 'codecOrder：自动时为空数组');

if (failures.length) {
  console.error(`流匹配规则检查失败 ${failures.length}/${checks}：\n` + failures.join('\n'));
  process.exit(1);
}
console.log(`流匹配规则检查通过：${checks} 项`);
