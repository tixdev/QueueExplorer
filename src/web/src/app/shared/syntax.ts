/** A run of text and the syntax class it is rendered with (empty: plain). */
export interface SyntaxToken {
  text: string;
  kind: '' | 'key' | 'string' | 'number' | 'literal' | 'punct' | 'tag' | 'attr' | 'value' | 'comment' | 'decl';
}

/** Bodies larger than this are shown indented but not coloured, to keep the page responsive. */
export const MAX_HIGHLIGHT_CHARS = 200_000;

/** Splits an (already indented) JSON or XML body into coloured runs. Unknown formats come back as one plain run. */
export function highlight(text: string, format: string): SyntaxToken[] {
  if (text.length > MAX_HIGHLIGHT_CHARS) return [{ text, kind: '' }];
  if (format === 'json') return highlightJson(text);
  if (format === 'xml') return highlightXml(text);
  return [{ text, kind: '' }];
}

const JSON_TOKEN = /("(?:[^"\\]|\\.)*")(\s*:)?|(-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?)|\b(true|false|null)\b|([{}[\],])/g;

function highlightJson(text: string): SyntaxToken[] {
  const tokens: SyntaxToken[] = [];
  let last = 0;
  for (const m of text.matchAll(JSON_TOKEN)) {
    if (m.index > last) tokens.push({ text: text.slice(last, m.index), kind: '' });
    if (m[1] !== undefined) {
      tokens.push({ text: m[1], kind: m[2] !== undefined ? 'key' : 'string' });
      if (m[2] !== undefined) tokens.push({ text: m[2], kind: 'punct' });
    } else if (m[3] !== undefined) tokens.push({ text: m[3], kind: 'number' });
    else if (m[4] !== undefined) tokens.push({ text: m[4], kind: 'literal' });
    else tokens.push({ text: m[5], kind: 'punct' });
    last = m.index + m[0].length;
  }
  if (last < text.length) tokens.push({ text: text.slice(last), kind: '' });
  return tokens;
}

const XML_TOKEN = /(<!--[\s\S]*?-->)|(<\?[\s\S]*?\?>|<!\[CDATA\[[\s\S]*?\]\]>|<![^>]*>)|(<\/?)([\w:.-]+)((?:\s+[\w:.-]+\s*=\s*(?:"[^"]*"|'[^']*'))*)\s*(\/?>)/g;
const XML_ATTR = /(\s+)([\w:.-]+)(\s*=\s*)("[^"]*"|'[^']*')/g;

function highlightXml(text: string): SyntaxToken[] {
  const tokens: SyntaxToken[] = [];
  let last = 0;
  for (const m of text.matchAll(XML_TOKEN)) {
    if (m.index > last) tokens.push({ text: text.slice(last, m.index), kind: '' });
    if (m[1] !== undefined) tokens.push({ text: m[1], kind: 'comment' });
    else if (m[2] !== undefined) tokens.push({ text: m[2], kind: 'decl' });
    else {
      tokens.push({ text: m[3] + m[4], kind: 'tag' });
      for (const a of (m[5] ?? '').matchAll(XML_ATTR)) {
        tokens.push({ text: a[1], kind: '' }, { text: a[2], kind: 'attr' }, { text: a[3], kind: 'punct' }, { text: a[4], kind: 'value' });
      }
      tokens.push({ text: m[0].endsWith('/>') ? '/>' : '>', kind: 'tag' });
    }
    last = m.index + m[0].length;
  }
  if (last < text.length) tokens.push({ text: text.slice(last), kind: '' });
  return tokens;
}
