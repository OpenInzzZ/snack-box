#!/usr/bin/env node
'use strict';

/**
 * 用 MCP 工具自动打通一局，验证"Agent 能自己玩"这条链路是否完整。
 * 通过标准 MCP stdio 协议跟 tools/agent/mcp-echo-corridor.js 说话（后者会按需拉起 headless 游戏）。
 *
 *   node tools/agent/agent-playthrough.js [low|medium|high]
 *
 * 流程：拿全图 → BFS 规划 → 一步步 echo_move → 确认通关 → 打印成绩与排行榜。
 * 这里用全图是为了把一局走完（属于作弊模式）；纯按 /state 的可见信息探索也能玩，只是慢。
 */

const path = require('path');
const { spawn } = require('child_process');

const DIFFICULTY = process.argv[2] || 'medium';

const server = spawn(process.execPath, [path.join(__dirname, 'mcp-echo-corridor.js')], {
  stdio: ['pipe', 'pipe', 'inherit'],
  env: { ...process.env, ECHO_DIFFICULTY: DIFFICULTY },
});

let nextId = 1;
const waiting = new Map();
let buffer = '';

server.stdout.setEncoding('utf8');
server.stdout.on('data', (chunk) => {
  buffer += chunk;

  let index;
  while ((index = buffer.indexOf('\n')) >= 0) {
    const line = buffer.slice(0, index).trim();
    buffer = buffer.slice(index + 1);
    if (!line) continue;

    const message = JSON.parse(line);
    const resolve = waiting.get(message.id);
    if (resolve) {
      waiting.delete(message.id);
      resolve(message);
    }
  }
});

function rpc(method, params) {
  const id = nextId++;
  return new Promise((resolve) => {
    waiting.set(id, resolve);
    server.stdin.write(`${JSON.stringify({ jsonrpc: '2.0', id, method, params })}\n`);
  });
}

async function callTool(name, args = {}) {
  const response = await rpc('tools/call', { name, arguments: args });
  if (response.error) throw new Error(response.error.message);

  const content = response.result.content[0];
  if (response.result.isError) throw new Error(content.text);
  return content.text;
}

const DIRECTIONS = [
  { name: 'north', bit: 1, dx: 0, dy: -1 },
  { name: 'east', bit: 2, dx: 1, dy: 0 },
  { name: 'south', bit: 4, dx: 0, dy: 1 },
  { name: 'west', bit: 8, dx: -1, dy: 0 },
];

function parseMap(text) {
  const rows = text.split('\n').map((line) => line.trim()).filter((line) => /^[0-9a-f-]+$/.test(line));
  const start = text.match(/起点\s*\[?(\d+),\s*(\d+)\]?/);
  const exit = text.match(/出口\s*\[?(\d+),\s*(\d+)\]?/);
  if (!rows.length || !start || !exit) throw new Error('echo_map 的输出格式不符合预期');

  return {
    rows,
    start: [Number(start[1]), Number(start[2])],
    exit: [Number(exit[1]), Number(exit[2])],
  };
}

function parsePlayerCell(text) {
  const match = text.match(/角色在第 (\d+),(\d+) 格/);
  if (!match) throw new Error('echo_state 的输出格式不符合预期');
  return [Number(match[1]), Number(match[2])];
}

/** 从 (fx,fy) 走到 (tx,ty) 的第一步方向 */
function nextDirection(map, from, to) {
  const key = (x, y) => `${x},${y}`;
  const previous = new Map([[key(from[0], from[1]), null]]);
  const queue = [from];

  while (queue.length) {
    const [x, y] = queue.shift();
    if (x === to[0] && y === to[1]) break;

    const mask = parseInt(map.rows[y][x], 16);
    for (const dir of DIRECTIONS) {
      if (!(mask & dir.bit)) continue;

      const nx = x + dir.dx;
      const ny = y + dir.dy;
      if (ny < 0 || ny >= map.rows.length || nx < 0 || nx >= map.rows[ny].length) continue;
      if (previous.has(key(nx, ny))) continue;

      previous.set(key(nx, ny), [x, y]);
      queue.push([nx, ny]);
    }
  }

  if (!previous.has(key(to[0], to[1]))) return null;

  let step = to;
  while (true) {
    const back = previous.get(key(step[0], step[1]));
    if (!back) return null;
    if (back[0] === from[0] && back[1] === from[1]) break;
    step = back;
  }

  const dx = step[0] - from[0];
  const dy = step[1] - from[1];
  return DIRECTIONS.find((dir) => dir.dx === dx && dir.dy === dy) || null;
}

async function main() {
  await rpc('initialize', { protocolVersion: '2024-11-05' });

  const mapText = await callTool('echo_map');
  const map = parseMap(mapText);
  console.log(`迷宫 ${map.rows[0].length}x${map.rows.length}，起点 ${map.start}，出口 ${map.exit}`);

  let stateText = await callTool('echo_state');
  let cell = parsePlayerCell(stateText);

  const maxSteps = map.rows.length * map.rows[0].length * 3 + 50;
  let step = 0;
  let stalls = 0;

  while (cell[0] !== map.exit[0] || cell[1] !== map.exit[1]) {
    if (++step > maxSteps) throw new Error(`走了 ${maxSteps} 步还没到出口，最后在 ${cell}`);

    const direction = nextDirection(map, cell, map.exit);
    if (!direction) throw new Error(`从 ${cell} 找不到通往出口的路`);

    stateText = await callTool('echo_move', { direction: direction.name });
    const moved = parsePlayerCell(stateText);

    if (moved[0] === cell[0] && moved[1] === cell[1]) {
      if (++stalls > 3) {
        console.error(`卡住时状态：\n${stateText}`);
        throw new Error(`在 ${cell} 朝 ${direction.name} 连续三次都没走动`);
      }
    } else {
      stalls = 0;
    }

    cell = moved;
  }

  const finalState = await callTool('echo_state');
  const won = /通关=true/.test(finalState);
  const elapsed = finalState.match(/用时 ([\d.]+) 秒/);

  console.log(`走到出口，共 ${step} 步，用时 ${elapsed ? elapsed[1] : '?'} 秒，通关=${won}`);

  if (!won) throw new Error('已站到出口但没有判定通关');

  const leaderboard = await callTool('echo_leaderboard');
  console.log(leaderboard);
  console.log('=== Agent 自行通关成功 ===');
}

main()
  .catch((error) => {
    console.error(`失败：${error.message}`);
    process.exitCode = 1;
  })
  .finally(() => {
    server.stdin.end();
  });
