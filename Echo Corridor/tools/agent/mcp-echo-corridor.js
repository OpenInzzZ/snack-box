#!/usr/bin/env node
'use strict';

/**
 * Echo Corridor 的 MCP 服务：把游戏内的 Agent 桥（HTTP）包装成标准 MCP 工具，
 * 让 Agent 能自己开一局、按方向键走位、看成绩。
 *
 * 零依赖，直接跑：
 *   node tools/agent/mcp-echo-corridor.js
 *
 * 可用的环境变量：
 *   GODOT_BIN         Godot 可执行文件（默认取本机 4.7 mono 版）
 *   ECHO_PROJECT      项目根目录（默认是本脚本的上两级）
 *   ECHO_AGENT_PORT   Agent 桥端口（默认 45871）
 *   ECHO_DIFFICULTY   自动启动时的难度 low/medium/high（默认 medium）
 *
 * 第一次调用工具时会自动以 headless 方式把游戏拉起来；已有实例在跑就直接连。
 */

const http = require('http');
const path = require('path');
const { spawn } = require('child_process');

const HOST = '127.0.0.1';
const PORT = Number(process.env.ECHO_AGENT_PORT || 45871);
const GODOT_BIN = process.env.GODOT_BIN
  || 'D:/Programs/Godot_v4.7-stable_mono_win64/Godot_v4.7-stable_mono_win64_console.exe';
const PROJECT = process.env.ECHO_PROJECT || path.resolve(__dirname, '..', '..');
const DIFFICULTY = process.env.ECHO_DIFFICULTY || 'medium';

let gameProcess = null;

// ---------- 与游戏通信 ----------

function request(method, urlPath, body) {
  return new Promise((resolve, reject) => {
    const payload = body === undefined ? null : Buffer.from(JSON.stringify(body), 'utf8');
    const req = http.request(
      {
        host: HOST,
        port: PORT,
        path: urlPath,
        method,
        headers: payload
          ? { 'Content-Type': 'application/json', 'Content-Length': payload.length }
          : {},
        timeout: 15000,
      },
      (res) => {
        let text = '';
        res.setEncoding('utf8');
        res.on('data', (chunk) => { text += chunk; });
        res.on('end', () => {
          try {
            resolve(JSON.parse(text));
          } catch (error) {
            reject(new Error(`游戏返回的不是 JSON：${text.slice(0, 200)}`));
          }
        });
      },
    );

    req.on('timeout', () => req.destroy(new Error('请求超时')));
    req.on('error', reject);
    if (payload) req.write(payload);
    req.end();
  });
}

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

/** 确保有一局游戏在跑：连得上就直接用，连不上就自己拉一个 headless 实例 */
async function ensureGame() {
  try {
    await request('GET', '/state');
    return '已连接到正在运行的游戏';
  } catch (error) {
    // 下面自己启动
  }

  if (!gameProcess) {
    gameProcess = spawn(
      GODOT_BIN,
      ['--headless', '--path', PROJECT, 'res://scenes/maze.tscn', '--',
        `--agent-port=${PORT}`, `--difficulty=${DIFFICULTY}`],
      { stdio: ['ignore', 'pipe', 'pipe'] },
    );
    gameProcess.stdout.on('data', () => {});
    gameProcess.stderr.on('data', () => {});
    gameProcess.on('exit', () => { gameProcess = null; });
  }

  const deadline = Date.now() + 30000;
  let lastError = null;
  while (Date.now() < deadline) {
    await sleep(500);
    try {
      await request('GET', '/state');
      return `已启动游戏（难度 ${DIFFICULTY}）并连接成功`;
    } catch (error) {
      lastError = error;
    }
  }

  throw new Error(`无法启动游戏：${lastError ? lastError.message : '超时'}`);
}

/** 把状态里的可见墙整理成一段便于阅读的文字 */
function describeVisible(state) {
  const lines = [];
  lines.push(`难度 ${state.difficulty}，外形 ${state.shape}，迷宫 ${state.grid.width}x${state.grid.height}` +
    `（形状内 ${state.grid.cells_in_shape} 格，格宽 ${state.grid.cell_size}）`);
  lines.push(`角色在第 ${state.player.cell[0]},${state.player.cell[1]} 格，用时 ${state.elapsed_seconds} 秒，` +
    `已移动 ${state.moves} 次，通关=${state.won}`);
  lines.push(`当前能听到 ${state.visible_walls.length} 段墙（共 ${state.walls_total} 段），` +
    `下面是这些墙的线段坐标：`);
  for (const wall of state.visible_walls) {
    lines.push(`  (${Math.round(wall.a[0])},${Math.round(wall.a[1])}) → ` +
      `(${Math.round(wall.b[0])},${Math.round(wall.b[1])})  亮度 ${wall.intensity}`);
  }
  return lines.join('\n');
}

// ---------- MCP 工具 ----------

const TOOLS = [
  {
    name: 'echo_state',
    description: '查看当前这一局里"玩家能感知到"的信息：位置、用时、以及正在发亮的墙壁坐标。看不到未点亮的地方。',
    inputSchema: { type: 'object', properties: {} },
  },
  {
    name: 'echo_map',
    description: '拿到整张迷宫的全图（文字图 + 每格通行方向）。这属于作弊，只建议用来规划或调试，' +
      '想按规则玩请只用 echo_state。',
    inputSchema: { type: 'object', properties: {} },
  },
  {
    name: 'echo_move',
    description: '让角色朝某个方向走一格（默认走到相邻格心就停，转急弯也不会卡在格边缘）。' +
      '撞墙时角色不会动，看返回里的格子坐标就知道有没有走成。' +
      '也可以用 frames 精确按帧数控制，例如 frames=60 表示按住约 1 秒。',
    inputSchema: {
      type: 'object',
      properties: {
        direction: { type: 'string', enum: ['north', 'south', 'east', 'west'], description: '方向' },
        frames: { type: 'integer', minimum: 1, maximum: 600, description: '可选：按住多少物理帧，不填就是走满一格' },
      },
      required: ['direction'],
    },
  },
  {
    name: 'echo_restart',
    description: '重开一张同难度的新迷宫。',
    inputSchema: { type: 'object', properties: {} },
  },
  {
    name: 'echo_reset',
    description: '换难度并重建迷宫，难度为 low / medium / high。',
    inputSchema: {
      type: 'object',
      properties: {
        difficulty: { type: 'string', enum: ['low', 'medium', 'high'], description: '难度' },
      },
      required: ['difficulty'],
    },
  },
  {
    name: 'echo_leaderboard',
    description: '查看三档难度的通关用时排行榜（按难度分开记录）。',
    inputSchema: { type: 'object', properties: {} },
  },
];

async function callTool(name, args) {
  switch (name) {
    case 'echo_state': {
      const state = await request('GET', '/state');
      return `${describeVisible(state)}\n\n原始 JSON：\n${JSON.stringify(state)}`;
    }

    case 'echo_map': {
      const truth = await request('GET', '/truth');
      return `${truth.note}\n起点 ${truth.start_cell}，出口 ${truth.exit_cell}\n` +
        `${truth.cells_encoding}\n\n${truth.map_ascii}\n\n每格通行方向（行=从上到下）：\n` +
        truth.cells.join('\n');
    }

    case 'echo_move': {
      const state = await request('POST', '/move', {
        direction: args.direction,
        frames: args.frames,
      });
      return describeVisible(state);
    }

    case 'echo_restart': {
      const state = await request('POST', '/restart', {});
      return `已重开新迷宫。\n${describeVisible(state)}`;
    }

    case 'echo_reset': {
      const ack = await request('POST', '/reset', { difficulty: args.difficulty });
      await sleep(1500);
      const state = await request('GET', '/state');
      return `${ack.message || '已重建'}\n${describeVisible(state)}`;
    }

    case 'echo_leaderboard': {
      const boards = await request('GET', '/leaderboard');
      const lines = ['三档难度的排行榜（单位：秒）'];
      for (const [difficulty, times] of Object.entries(boards)) {
        lines.push(`  ${difficulty}：${times.length ? times.map((t) => t.toFixed(2)).join('  ') : '暂无成绩'}`);
      }
      return lines.join('\n');
    }

    default:
      throw new Error(`未知工具 ${name}`);
  }
}

// ---------- MCP stdio 循环 ----------

function send(message) {
  process.stdout.write(`${JSON.stringify(message)}\n`);
}

async function handle(message) {
  const { id, method, params } = message;

  if (method === 'initialize') {
    return {
      jsonrpc: '2.0',
      id,
      result: {
        protocolVersion: (params && params.protocolVersion) || '2024-11-05',
        capabilities: { tools: {} },
        serverInfo: { name: 'echo-corridor', version: '1.0.0' },
      },
    };
  }

  if (method === 'notifications/initialized' || method === 'notifications/cancelled') return null;

  if (method === 'ping') return { jsonrpc: '2.0', id, result: {} };

  if (method === 'tools/list') return { jsonrpc: '2.0', id, result: { tools: TOOLS } };

  if (method === 'tools/call') {
    const name = params && params.name;
    const args = (params && params.arguments) || {};

    try {
      await ensureGame();
      const text = await callTool(name, args);
      return { jsonrpc: '2.0', id, result: { content: [{ type: 'text', text }] } };
    } catch (error) {
      return {
        jsonrpc: '2.0',
        id,
        result: { content: [{ type: 'text', text: `调用失败：${error.message}` }], isError: true },
      };
    }
  }

  return { jsonrpc: '2.0', id, error: { code: -32601, message: `不支持的方法 ${method}` } };
}

let pending = '';
let inFlight = 0;
let stdinClosed = false;

/** stdin 关了、且没有未完成的请求时才退出，否则会把异步工具调用掐死 */
function exitWhenIdle() {
  if (!stdinClosed || inFlight > 0) return;
  if (gameProcess) gameProcess.kill();
  process.exit(0);
}

process.stdin.setEncoding('utf8');
process.stdin.on('data', (chunk) => {
  pending += chunk;

  let index;
  while ((index = pending.indexOf('\n')) >= 0) {
    const line = pending.slice(0, index).trim();
    pending = pending.slice(index + 1);
    if (!line) continue;

    let message;
    try {
      message = JSON.parse(line);
    } catch (error) {
      continue;
    }

    inFlight++;
    Promise.resolve(handle(message))
      .then((response) => {
        if (response) send(response);
      })
      .catch((error) => {
        if (message && message.id !== undefined) {
          send({ jsonrpc: '2.0', id: message.id, error: { code: -32603, message: error.message } });
        }
      })
      .finally(() => {
        inFlight--;
        exitWhenIdle();
      });
  }
});

process.stdin.on('end', () => {
  stdinClosed = true;
  exitWhenIdle();
});

process.on('SIGTERM', () => {
  if (gameProcess) gameProcess.kill();
  process.exit(0);
});
