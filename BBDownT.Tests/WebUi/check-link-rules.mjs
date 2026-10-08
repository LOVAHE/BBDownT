// 用 BBDownT.Tests/TestData/link-rules.json 检查网页前端的链接规则(index.html 里 link-rules:begin 到 link-rules:end 之间的纯函数)。
// 运行：node BBDownT.Tests/WebUi/check-link-rules.mjs   (C# 测试 WebUiLinkRulesTests 在装有 node 时会自动运行它)
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const root = join(here, '..', '..');
const html = readFileSync(join(root, 'BBDownT', 'WebUi', 'index.html'), 'utf8');
const begin = html.indexOf('// link-rules:begin');
const end = html.indexOf('// link-rules:end');
if (begin < 0 || end < begin) {
  console.error('index.html 里找不到 link-rules:begin / link-rules:end 标记');
  process.exit(2);
}
const code = html.slice(html.indexOf('\n', begin) + 1, end);
const rules = new Function(`${code}\nreturn { splitInput, cleanLink, linkKey, isSpaceUrl, linkKind, pagesExpr };`)();
const data = JSON.parse(readFileSync(join(root, 'BBDownT.Tests', 'TestData', 'link-rules.json'), 'utf8'));

const failures = [];
let checks = 0;
const eq = (a, b) => JSON.stringify(a) === JSON.stringify(b);
const check = (ok, message) => { checks++; if (!ok) failures.push(message); };
const firstLink = (input) => {
  const { links } = rules.splitInput(input);
  return links.length ? links[0] : input;
};

for (const c of data.space) {
  check(rules.isSpaceUrl(c.url) === c.expected, `isSpaceUrl(${c.url}) 应为 ${c.expected}`);
}
for (const c of data.split) {
  const actual = rules.splitInput(c.input);
  check(eq(actual.links, c.links) && eq(actual.ignored, c.ignored),
    `splitInput(${JSON.stringify(c.input)}) = ${JSON.stringify(actual)}，应为 ${JSON.stringify({ links: c.links, ignored: c.ignored })}`);
}
for (const c of data.linkKey) {
  const keys = (c.same || c.different).map((x) => rules.linkKey(firstLink(x)));
  if (c.same) check(keys.every((k) => k === keys[0]), `linkKey 应相同：${JSON.stringify(c.same)} -> ${JSON.stringify(keys)}`);
  else check(new Set(keys).size === keys.length, `linkKey 应不同：${JSON.stringify(c.different)} -> ${JSON.stringify(keys)}`);
}
for (const c of data.kind) {
  const actual = rules.linkKind(c.link);
  check(actual === c.expected, `linkKind(${c.link}) = ${actual}，应为 ${c.expected}`);
}
for (const c of data.clean || []) {
  const actual = rules.cleanLink(c.input);
  check(actual === c.expected, `cleanLink(${JSON.stringify(c.input)}) = ${actual}，应为 ${c.expected}`);
}
for (const c of data.pagesExpr) {
  const actual = rules.pagesExpr(new Set(c.pages), c.total);
  check(actual === c.expected, `pagesExpr(${JSON.stringify(c.pages)}, ${c.total}) = ${actual}，应为 ${c.expected}`);
}

if (failures.length) {
  console.error(`链接规则检查失败 ${failures.length}/${checks}：\n` + failures.join('\n'));
  process.exit(1);
}
console.log(`链接规则检查通过：${checks} 项`);
