#!/usr/bin/env node
'use strict';

/**
 * 本地假模型：按 OpenAI 的两种格式回话，用来验证「AI 代打」的请求拼装与回复解析。
 * 它把收到的每个请求体写进日志，方便断言格式是否正确。
 *
 *   node tools/checks/mock-llm.js <日志文件> <端口> [响应延迟毫秒]
 *
 * 第 4 个参数把回复拖慢，用来验证「点了停止能不能立刻打断正在飞的请求」。
 */

const http = require('http');
const fs = require('fs');

const LOG_PATH = process.argv[2] || 'tools/out/mock-llm.log';
const PORT = Number(process.argv[3] || 45910);
const DELAY = Number(process.argv[4] || 0);

let count = 0;

const server = http.createServer((request, response) => {
  let body = '';
  request.on('data', (chunk) => { body += chunk; });
  request.on('error', () => {});
  response.on('error', () => {});
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

    const send = () => {
      // 延迟期间客户端可能已经断开（玩家点了停止），这时不能再往这个 socket 写
      if (response.writableEnded || response.destroyed) return;

      try {
        response.writeHead(200, { 'Content-Type': 'application/json', 'Content-Length': Buffer.byteLength(encoded) });
        response.end(encoded);
      } catch (error) {
        console.log(`[MOCK] 回复写入失败（客户端大概已断开）：${error.message}`);
      }
    };

    if (DELAY > 0) setTimeout(send, DELAY);
    else send();
  });
});

server.listen(PORT, '127.0.0.1', () => console.log(`[MOCK] 假模型已启动：http://127.0.0.1:${PORT}/v1`));
