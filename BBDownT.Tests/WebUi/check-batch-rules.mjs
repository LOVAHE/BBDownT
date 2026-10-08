// 检查网页前端「多个链接逐个解析」的纯函数(index.html 里 batch-rules:begin 到 batch-rules:end 之间)，
// 以及它用到的 link-rules、stream-pref 两段：去重、上限、解析顺序与进度、每行下载时用的流和下载请求、按上次的选择自动选中。
// 运行：node BBDownT.Tests/WebUi/check-batch-rules.mjs   (C# 测试 WebUiLinkRulesTests 在装有 node 时会自动运行它)
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const root = join(here, '..', '..');
const html = readFileSync(join(root, 'BBDownT', 'WebUi', 'index.html'), 'utf8');
const section = (name) => {
  const begin = html.indexOf(`// ${name}:begin`);
  const end = html.indexOf(`// ${name}:end`);
  if (begin < 0 || end < begin) {
    console.error(`index.html 里找不到 ${name}:begin / ${name}:end 标记`);
    process.exit(2);
  }
  return html.slice(html.indexOf('\n', begin) + 1, end);
};
const code = [section('link-rules'), section('stream-pref'), section('batch-rules')].join('\n');
const rules = new Function(`${code}\nreturn { BATCH_MAX, batchPlan, batchProgress, nextBatchRow, rowStreams, pinStreams, batchRowRequest, pickByPref, codecOrder };`)();

const failures = [];
let checks = 0;
const check = (ok, message) => { checks++; if (!ok) failures.push(message); };
const eq = (actual, expected, message) => {
  const a = JSON.stringify(actual);
  const e = JSON.stringify(expected);
  check(a === e, `${message}：得到 ${a}，应为 ${e}`);
};

// ---------- batchPlan：去重、分类、上限 ----------
const BV = (n) => `BV1${String(n).padStart(9, 'A')}`;
const plan = (links, max = rules.BATCH_MAX) => rules.batchPlan(links, max);
const kinds = (p) => p.rows.map((r) => r.kind);

check(rules.BATCH_MAX === 20, `BATCH_MAX 应为 20，得到 ${rules.BATCH_MAX}`);
eq(plan([BV(1)]).batch, false, '只有一个链接：仍按单个链接处理');
eq(plan([BV(1), BV(2)]).batch, true, '两个视频：逐个解析');
eq(plan([BV(1), `https://www.bilibili.com/video/${BV(1)}?p=1&spm_id_from=x`]).rows.length, 2, '带 p=1 的链接与BV号的键不同（分P有意义），不去重');
{
  const p = plan([BV(1), `https://www.bilibili.com/video/${BV(1)}/?spm_id_from=333`, `https://m.bilibili.com/video/${BV(1)}`]);
  eq(p.rows.length, 1, '同一视频的BV号、完整链接、手机版链接只算一个');
  eq(p.batch, false, '去重后只剩一个链接：按单个链接处理');
  eq(p.rows[0].link, BV(1), '去重时保留第一次出现的写法');
}
eq(plan([`https://www.bilibili.com/video/${BV(1)}`, `https://www.bilibili.com/video/${BV(1)}`]).rows.length, 1, '完全相同的链接只算一个');
eq(kinds(plan([BV(1), 'https://space.bilibili.com/123', 'https://space.bilibili.com/123/favlist?fid=9', 'https://www.bilibili.com/medialist/detail/ml1', 'cheese/ss12', 'https://www.bilibili.com/cheese/play/ss34'])),
  ['single', 'list', 'list', 'list', 'list', 'list'], 'UP主空间、收藏夹、合集、课程都是列表链接，不解析');
eq(kinds(plan(['ep123', 'ss45', 'https://b23.tv/abc', 'https://example.com/x'])), ['single', 'single', 'single', 'unknown'], '番剧单集、短链可解析；认不出的链接不解析');
eq(plan(['https://space.bilibili.com/1', 'https://space.bilibili.com/2']).batch, false, '只有列表链接：不逐个解析（与以前相同）');
eq(plan([BV(1), 'https://space.bilibili.com/1']).batch, true, '一个视频加一个列表链接：视频逐个解析，列表链接按原方式');
{
  const links = Array.from({ length: 23 }, (_, i) => BV(i + 1));
  const p = plan(['https://space.bilibili.com/9', ...links]);
  eq(p.over, 3, '23 个视频：超过上限 3 个');
  eq(p.rows.filter((r) => r.kind === 'single').length, 20, '只解析前 20 个视频');
  eq(p.rows.slice(-3).map((r) => r.kind), ['over', 'over', 'over'], '超过上限的按顺序在最后');
  eq(p.rows[0].kind, 'list', '列表链接不占上限');
}
eq(plan([BV(1), BV(2), BV(3)], 2).rows.map((r) => r.kind), ['single', 'single', 'over'], '上限可调（max 参数）');
eq(plan([BV(1), BV(2), BV(1)]).rows.map((r) => r.key), ['BV' + BV(1).slice(2), 'BV' + BV(2).slice(2)], '重复的链接去掉，顺序不变');

// ---------- 解析顺序与进度 ----------
{
  const rows = [
    { kind: 'list', status: 'list' },
    { kind: 'single', status: 'done' },
    { kind: 'single', status: 'parsing' },
    { kind: 'single', status: 'queued', id: 'a' },
    { kind: 'single', status: 'queued', id: 'b' },
    { kind: 'single', status: 'failed' },
    { kind: 'single', status: 'idle' },
    { kind: 'over', status: 'over' },
  ];
  eq(rules.batchProgress(rows), { total: 5, done: 2, pending: 3, failed: 1 }, '进度：不含列表、超过上限和未开始（idle）的行');
  eq(rules.nextBatchRow(rows).id, 'a', '按输入顺序取下一个排队的行');
  eq(rules.nextBatchRow(rows.filter((r) => r.status !== 'queued')), null, '没有排队的行时为 null');
  eq(rules.nextBatchRow([{ kind: 'over', status: 'queued' }]), null, '超过上限的行不解析');
}

// ---------- 每行下载时用的流、下载请求 ----------
const V = (id, quality, codec, size = 100) => ({ Key: `${id}:${codec}`, Id: String(id), Quality: quality, Codec: codec, Size: size });
const A = (id, label) => ({ Id: String(id), Label: label, Codec: 'M4A', Kind: 'normal', Size: 10 });
const res = (over = {}) => ({
  Videos: [V(120, '4K 超清', 'HEVC'), V(80, '1080P 高清', 'AVC'), V(80, '1080P 高清', 'HEVC'), V(64, '720P 高清', 'AVC')],
  Audios: [A(30280, '192K'), A(30232, '132K')],
  DefaultVideo: '80:AVC', DefaultAudio: '30280', Progressive: false, Api: 'WEB',
  Account: { WebLoggedIn: true, ApiAuthenticated: true }, Hints: [], ...over,
});
const pk = (over = {}) => ({ video: null, audio: null, autoVideo: null, autoAudio: null, skipVideoPref: false, skipAudioPref: false, ...over });
const both = { video: true, audio: true };
const brief = (s) => [s.video ? s.video.Key : null, s.videoHow, s.audio ? s.audio.Id : null, s.audioHow];

eq(brief(rules.rowStreams(res(), pk(), both)), ['80:AVC', 'default', '30280', 'default'], '没选：显示默认的流（按「画质」等设置）');
eq(brief(rules.rowStreams(res(), pk({ video: '120:HEVC', autoVideo: 'exact', audio: '30232' }), both)), ['120:HEVC', 'exact', '30232', 'manual'], '自动选中与点选分别标出');
eq(brief(rules.rowStreams(res(), pk({ video: '120:HEVC' }), { video: false, audio: true })), [null, 'default', '30280', 'default'], '「仅音频」：不显示视频流');
eq(brief(rules.rowStreams(res({ Progressive: true }), pk({ video: '120:HEVC' }), both)), ['80:AVC', 'default', '30280', 'default'], '合并流：只能下载默认的流');
eq(brief(rules.rowStreams(res(), pk({ video: '999:AV1' }), both)), ['80:AVC', 'default', '30280', 'default'], '选中的流不在结果里：按默认');

const base = () => ({ Url: BV(1), DfnPriority: '1080P 高码率,1080P 高帧率,1080P 高清,720P 高清' });
eq(rules.pinStreams(base(), res(), pk(), both), base(), '什么都没选：请求与不解析时完全相同');
eq(rules.pinStreams(base(), res(), pk({ video: '120:HEVC', autoVideo: 'lower', audio: '30232' }), both),
  { ...base(), VideoStream: '120:HEVC', DfnPriority: '4K 超清', EncodingPriority: 'hevc', AudioStream: '30232' }, '指定视频流和音频流，画质和编码改成这条流的');
eq(rules.pinStreams(base(), res(), pk({ video: '120:HEVC', audio: '30232' }), { video: false, audio: true }),
  { ...base(), AudioStream: '30232' }, '「仅音频」：只指定音频流');
eq(rules.pinStreams(base(), res(), pk({ video: '120:HEVC', audio: '30232' }), { video: false, audio: false }), base(), '「仅字幕」等：不指定流');
eq(rules.pinStreams(base(), res({ Progressive: true }), pk({ video: '80:AVC' }), both), base(), '合并流：不指定视频流');
eq(rules.pinStreams(base(), res({ Videos: [V(80, '1080P 高清', 'XYZ')] }), pk({ video: '80:XYZ' }), both),
  { ...base(), VideoStream: '80:XYZ', DfnPriority: '1080P 高清' }, '认不出的编码：不改「视频编码优先」');

const row = (status, over = {}) => ({ status, res: res(), pick: pk({ video: '120:HEVC', autoVideo: 'exact' }), ...over });
eq(rules.batchRowRequest(base(), row('done'), both).VideoStream, '120:HEVC', '已解析的行：按这一行的流指定');
eq(rules.batchRowRequest(base(), row('failed'), both), base(), '解析失败的行：按「画质」等设置提交');
eq(rules.batchRowRequest(base(), row('queued'), both), base(), '还没解析完（排队重新解析）的行：不用旧结果');
eq(rules.batchRowRequest(base(), row('parsing'), both), base(), '正在解析的行：不用旧结果');
eq(rules.batchRowRequest(base(), { status: 'list', res: null, pick: pk() }, both), base(), '列表链接：按原方式提交');

// ---------- pickByPref：按上次的选择自动选中（单个链接和每一行共用） ----------
const pref = { video: { Qn: '120', Quality: '4K 超清', Codec: 'HEVC' }, audio: { Id: '30280', Label: '192K' } };
const order = rules.codecOrder('');
{
  const p = pk();
  const st = rules.pickByPref(res(), p, pref, '', order);
  eq([p.video, p.autoVideo, p.audio, p.autoAudio], ['120:HEVC', 'exact', '30280', 'exact'], '与上次相同的流：自动选中');
  eq([st.video.how, st.audio.how], ['exact', 'exact'], '提示结果为 exact');
}
{
  const p = pk();
  const st = rules.pickByPref(res(), p, pref, 'guest', order);
  eq([p.video, p.audio, st.video.how, st.audio.how], [null, null, 'guest', 'guest'], '按未登录身份解析（APP/TV 没有凭证等）：不自动选择');
}
{
  const p = pk({ video: '64:AVC' });
  rules.pickByPref(res(), p, pref, '', order);
  eq([p.video, p.autoVideo, p.audio], ['64:AVC', null, '30280'], '已点选的视频流不被覆盖，音频仍自动选中');
}
{
  const p = pk({ skipVideoPref: true });
  rules.pickByPref(res(), p, pref, '', order);
  eq(p.video, '120:HEVC', 'skip 为 true：相同的流仍自动选中（只跳过换选的流）');
  const q = pk({ skipVideoPref: true });
  const st = rules.pickByPref(res({ Videos: [V(80, '1080P 高清', 'HEVC')] }), q, pref, '', order);
  eq([q.video, st.video.how], [null, 'skipped'], 'skip 为 true：换选的流（lower）跳过');
  const w = pk({ skipVideoPref: 'all', skipAudioPref: 'all' });
  const st2 = rules.pickByPref(res(), w, pref, '', order);
  eq([w.video, w.audio, st2.video.how, st2.audio.how], [null, null, 'skipped', 'skipped'], "skip 为 'all'（在这一行取消过）：相同的流也不自动选择");
}
{
  const p = pk();
  const st = rules.pickByPref(res(), p, { video: null, audio: null }, '', order);
  eq([p.video, p.audio, st.video, st.audio], [null, null, null, null], '没有上次的选择：不自动选择');
}
{
  const p = pk();
  rules.pickByPref(res({ Progressive: true }), p, pref, '', order);
  eq([p.video, p.audio], [null, '30280'], '合并流：不自动选视频流');
}

if (failures.length) {
  console.error(`check-batch-rules：${failures.length}/${checks} 项不通过\n` + failures.join('\n'));
  process.exit(1);
}
console.log(`check-batch-rules：${checks} 项全部通过`);
