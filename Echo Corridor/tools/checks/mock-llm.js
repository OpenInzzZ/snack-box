#!/usr/bin/env node
'use strict';

/**
 * 本地假模型：按 OpenAI 的两种格式回话，用来验证「AI 代打」的请求拼装与回复解析。
 * 它把收到的每个请求体写进日志，方便断言格式是否正确。
 *
 *   node tools/checks/mock-llm.js <日志文件> <端口>
 */

const http = require('http');
const fs = require('fs');

const LOG_PATH = process.argv[2] || 'tools/out/mock-llm.log';
const PORT = Number(process.argv[3] || 45910);

let count = 0;

const server = http.createServer((request, response) => {
  let body = '';
  request.on('data', (chunk) => { body += chunk; });
  request.on('end', () => {
    count++;

    fs.appendFileSync(LOG_PATH, `=== ${request.method} ${request.url} ===\n${body}\n`);

    const isResponses = request.url.includes('/responses');
    const direction = ['north', 'east', 'south', 'west'][count % 4];
    const text = `第${count}步：往${direction}走一格。`;

    const payload = isResponses
      ? {
        output: [
          { type: 'reasoning', summary: [{ type: 'summary_text', text: '先想想要往哪边走。' }] },
          { type: 'message', content: [{ type: 'output_text', text }] },
          { type: 'function_call', call_id: `call-${count}`, name: 'echo_move', arguments: JSON.stringify({ direction }) },
        ],
      }
      : {
        choices: [{
          message: {
            role: 'assistant',
            content: text,
            reasoning_content: '先想想要往哪边走。',
            tool_calls: [{
              id: `call-${count}`,
              type: 'function',
              function: { name: 'echo_move', arguments: JSON.stringify({ direction }) },
            }],
          },
        }],
      };

    const encoded = JSON.stringify(payload);
    response.writeHead(200, { 'Content-Type': 'application/json', 'Content-Length': Buffer.byteLength(encoded) });
    response.end(encoded);
  });
});

server.listen(PORT, '127.0.0.1', () => console.log(`[MOCK] 假模型已启动：http://127.0.0.1:${PORT}/v1`));
