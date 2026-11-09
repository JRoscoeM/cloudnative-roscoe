// The organizer dashboard. Everything from the server is written with
// textContent, so a room name can never become markup.
const REFRESH_MS = 15_000;
const TOKEN_KEY = 'headroom.organizer';
const SVG = 'http://www.w3.org/2000/svg';

const $ = (id) => document.getElementById(id);
const signIn = $('sign-in');
const dashboard = $('dashboard');
const status = $('dashboard-status');

let timer;

function token() {
  try {
    return sessionStorage.getItem(TOKEN_KEY);
  } catch {
    return null;
  }
}

function saveToken(value) {
  try {
    if (value) {
      sessionStorage.setItem(TOKEN_KEY, value);
    } else {
      sessionStorage.removeItem(TOKEN_KEY);
    }
  } catch {
    // Without storage the organizer just signs in again on reload.
  }
}

function showSignIn(message = '') {
  clearTimeout(timer);
  saveToken(null);
  dashboard.hidden = true;
  signIn.hidden = false;
  $('sign-in-status').textContent = message;
}

async function api(path, options = {}) {
  const response = await fetch(`/api/organizer${path}`, {
    ...options,
    headers: {
      Authorization: `Bearer ${token()}`,
      ...(options.body ? { 'Content-Type': 'application/json' } : {}),
    },
  });

  if (response.status === 401) {
    showSignIn('Please sign in again.');
    throw new Error('Signed out');
  }

  const data = response.status === 204 ? null : await response.json();
  if (!response.ok) {
    throw new Error(data?.error ?? `The server answered ${response.status}.`);
  }
  return data;
}

function element(tag, className, text) {
  const node = document.createElement(tag);
  if (className) {
    node.className = className;
  }
  if (text !== undefined) {
    node.textContent = text;
  }
  return node;
}

function number(value, digits = 0) {
  return value === null || value === undefined ? '–' : value.toFixed(digits);
}

function renderTiles(health) {
  const pods = health.podsReady === null ? '–' : `${health.podsReady} of ${health.podsDesired}`;
  const tiles = [
    ['Requests per minute', number(health.requestsPerMinute, 1)],
    ['Error rate', health.errorPercent === null ? '–' : `${number(health.errorPercent, 1)}%`],
    ['Response time (p95)', health.latencyP95Ms === null ? '–' : `${number(health.latencyP95Ms)} ms`],
    ['Pods ready', pods],
    ['Restarts, last hour', number(health.restartsLastHour)],
    ['Memory, busiest pod', health.memoryPercent === null ? '–' : `${number(health.memoryPercent)}%`],
    ['Readings per minute', number(health.readingsPerMinute, 1)],
  ];

  $('tiles').replaceChildren(...tiles.map(([label, value]) => {
    const tile = element('div', 'tile');
    tile.append(element('span', 'tile-value', value), element('span', 'tile-label', label));
    return tile;
  }));
}

function svgElement(name, attributes) {
  const node = document.createElementNS(SVG, name);
  Object.entries(attributes).forEach(([key, value]) => node.setAttribute(key, value));
  return node;
}

function renderChart(title, points, format) {
  const chart = element('figure', 'chart');
  chart.append(element('figcaption', 'chart-title', title));

  if (points.length < 2) {
    chart.append(element('p', 'chart-empty', 'Not enough data yet'));
    return chart;
  }

  const width = 320;
  const height = 120;
  const left = 8;
  const right = 8;
  const top = 18;
  const bottom = 20;
  const plotWidth = width - left - right;
  const plotHeight = height - top - bottom;
  const peak = Math.max(...points.map((point) => point.value), 0.001);
  const start = points[0].time;
  const span = points[points.length - 1].time - start || 1;
  const x = (point) => left + ((point.time - start) / span) * plotWidth;
  const y = (point) => top + plotHeight - (point.value / peak) * plotHeight;

  const svg = svgElement('svg', {
    viewBox: `0 0 ${width} ${height}`,
    role: 'img',
    'aria-label': `${title}: latest ${format(points[points.length - 1].value)}, peak ${format(peak)}, over the last 30 minutes`,
  });

  svg.append(svgElement('line', { class: 'chart-axis', x1: left, x2: width - right, y1: top + plotHeight, y2: top + plotHeight }));
  svg.append(svgElement('path', {
    class: 'chart-line',
    d: points.map((point, index) => `${index ? 'L' : 'M'}${x(point).toFixed(1)} ${y(point).toFixed(1)}`).join(' '),
  }));

  const peakLabel = svgElement('text', { class: 'chart-label', x: left, y: 12 });
  peakLabel.textContent = `peak ${format(peak)}`;
  const startLabel = svgElement('text', { class: 'chart-label', x: left, y: height - 6 });
  startLabel.textContent = '30 min ago';
  const endLabel = svgElement('text', { class: 'chart-label', x: width - right, y: height - 6, 'text-anchor': 'end' });
  endLabel.textContent = 'now';
  svg.append(peakLabel, startLabel, endLabel);

  const crosshair = svgElement('line', { class: 'chart-crosshair', y1: top, y2: top + plotHeight, visibility: 'hidden' });
  const dot = svgElement('circle', { class: 'chart-dot', r: 4, visibility: 'hidden' });
  svg.append(crosshair, dot);

  const tooltip = element('div', 'chart-tip');
  tooltip.hidden = true;

  svg.addEventListener('pointermove', (event) => {
    const box = svg.getBoundingClientRect();
    const position = ((event.clientX - box.left) / box.width) * width;
    const point = points.reduce((best, candidate) =>
      Math.abs(x(candidate) - position) < Math.abs(x(best) - position) ? candidate : best);

    crosshair.setAttribute('x1', x(point));
    crosshair.setAttribute('x2', x(point));
    crosshair.setAttribute('visibility', 'visible');
    dot.setAttribute('cx', x(point));
    dot.setAttribute('cy', y(point));
    dot.setAttribute('visibility', 'visible');

    const time = new Date(point.time * 1000).toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' });
    tooltip.textContent = `${time}: ${format(point.value)}`;
    tooltip.hidden = false;
    tooltip.style.left = `${(x(point) / width) * 100}%`;
  });

  svg.addEventListener('pointerleave', () => {
    crosshair.setAttribute('visibility', 'hidden');
    dot.setAttribute('visibility', 'hidden');
    tooltip.hidden = true;
  });

  chart.append(svg, tooltip);
  return chart;
}

function renderHealth(health) {
  const overall = $('overall');
  overall.textContent = health.overall;
  overall.className = `pill ${health.overall === 'Healthy' ? 'level-open' : 'level-full'}`;
  $('metrics-notice').hidden = health.metricsAvailable;

  renderTiles(health);

  $('charts').replaceChildren(
    renderChart('Requests per minute', health.requestSeries, (value) => value.toFixed(1)),
    renderChart('Error rate', health.errorSeries, (value) => `${value.toFixed(1)}%`),
    renderChart('Response time (p95)', health.latencySeries, (value) => `${value.toFixed(0)} ms`),
  );

  const levels = { ok: 'level-open', down: 'level-full', off: 'level-unknown' };
  const labels = { ok: 'OK', down: 'Down', off: 'Off' };
  $('dependencies').replaceChildren(...health.dependencies.map((dependency) => {
    const row = element('li', 'dependency');
    row.append(
      element('span', 'dependency-name', dependency.name),
      element('span', 'dependency-detail', dependency.detail),
      element('span', `pill ${levels[dependency.state]}`, labels[dependency.state]),
    );
    return row;
  }));
}

function sensorLevel(state) {
  return { live: 'level-open', stale: 'level-packed' }[state] ?? 'level-unknown';
}

async function run(action, success) {
  status.textContent = 'Saving…';
  try {
    await action();
    status.textContent = success;
    await refresh();
  } catch (error) {
    if (error.message !== 'Signed out') {
      status.textContent = error.message;
    }
  }
}

async function showSensorLink(id, row) {
  try {
    const { path } = await api(`/sensor-links/${encodeURIComponent(id)}`, { method: 'POST' });
    const url = `${location.origin}${path}`;

    row.querySelector('.sensor-link')?.remove();
    const area = element('div', 'sensor-link');
    area.append(element('label', '', 'Open this link on the sensor phone. It only works for this space.'));

    const field = element('input');
    field.readOnly = true;
    field.value = url;
    field.setAttribute('aria-label', 'Sensor setup link');

    const copy = element('button', 'button button-quiet', 'Copy');
    copy.type = 'button';
    copy.addEventListener('click', async () => {
      field.select();
      try {
        await navigator.clipboard.writeText(url);
        copy.textContent = 'Copied';
      } catch {
        copy.textContent = 'Select and copy';
      }
    });

    area.append(field, copy);
    row.append(area);
  } catch (error) {
    if (error.message !== 'Signed out') {
      status.textContent = error.message;
    }
  }
}

function buildRow(space, kind) {
  const isRoom = kind === 'room';
  const row = element('div', 'space-row');
  row.dataset.id = space.id;

  const name = element('input', 'space-name');
  name.maxLength = 40;
  name.setAttribute('aria-label', isRoom ? 'Room name' : 'Line name');

  const amount = element('input', 'space-amount');
  amount.type = 'number';
  amount.min = 1;
  amount.max = isRoom ? 100000 : 3600;
  amount.setAttribute('aria-label', isRoom ? 'Capacity' : 'Seconds per person');

  const unit = element('span', 'space-unit', isRoom ? 'people' : 'sec / person');
  const sensor = element('span', 'pill space-sensor');

  const save = element('button', 'button', 'Save');
  save.type = 'button';
  save.addEventListener('click', () => {
    const body = isRoom
      ? { name: name.value, capacity: Number(amount.value) }
      : { name: name.value, secondsPerPerson: Number(amount.value) };
    row.dataset.dirty = '';
    run(() => api(`/${isRoom ? 'rooms' : 'queues'}/${encodeURIComponent(space.id)}`, {
      method: 'PUT',
      body: JSON.stringify(body),
    }), 'Saved.');
  });

  const link = element('button', 'button button-quiet', 'Sensor link');
  link.type = 'button';
  link.addEventListener('click', () => showSensorLink(space.id, row));

  const remove = element('button', 'button button-quiet', 'Delete');
  remove.type = 'button';
  remove.addEventListener('click', () => {
    if (confirm(`Delete ${name.value}? It disappears from the map right away.`)) {
      run(() => api(`/${isRoom ? 'rooms' : 'queues'}/${encodeURIComponent(space.id)}`, { method: 'DELETE' }), 'Deleted.');
    }
  });

  [name, amount].forEach((field) => field.addEventListener('input', () => { row.dataset.dirty = 'true'; }));

  const actions = element('div', 'space-actions');
  actions.append(save, link, remove);

  const fields = element('div', 'space-fields');
  fields.append(name, amount, unit, sensor);
  row.append(fields, actions);
  return row;
}

function syncRows(container, spaces, kind) {
  const isRoom = kind === 'room';
  const existing = new Map([...container.children].map((row) => [row.dataset.id, row]));

  const rows = spaces.map((space) => {
    const row = existing.get(space.id) ?? buildRow(space, kind);
    const [name, amount] = row.querySelectorAll('input');
    const sensor = row.querySelector('.space-sensor');

    // A refresh must never overwrite what someone is typing.
    if (row.dataset.dirty !== 'true') {
      name.value = space.name;
      amount.value = isRoom ? space.capacity : space.secondsPerPerson;
    }

    sensor.textContent = space.sensor.detail;
    sensor.className = `pill space-sensor ${sensorLevel(space.sensor.state)}`;
    return row;
  });

  container.replaceChildren(...rows);
}

async function refresh() {
  clearTimeout(timer);
  try {
    const overview = await api('/overview');
    signIn.hidden = true;
    dashboard.hidden = false;
    $('sample-notice').hidden = !overview.isSample;
    renderHealth(overview.health);
    syncRows($('room-rows'), overview.rooms, 'room');
    syncRows($('queue-rows'), overview.queues, 'queue');
  } catch (error) {
    if (error.message === 'Signed out') {
      return;
    }
    status.textContent = error.message;
  }
  timer = setTimeout(refresh, REFRESH_MS);
}

signIn.addEventListener('submit', async (event) => {
  event.preventDefault();
  const message = $('sign-in-status');
  message.textContent = 'Signing in…';

  const response = await fetch('/api/organizer/session', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ key: $('organizer-key').value }),
  });
  const data = response.status === 429 ? { error: 'Too many attempts. Please wait a minute.' } : await response.json();
  if (!response.ok) {
    message.textContent = data.error;
    return;
  }

  $('organizer-key').value = '';
  message.textContent = '';
  saveToken(data.token);
  refresh();
});

$('add-room').addEventListener('submit', (event) => {
  event.preventDefault();
  const form = event.target;
  run(() => api('/rooms', {
    method: 'POST',
    body: JSON.stringify({ name: form.name.value, capacity: Number(form.capacity.value) }),
  }).then(() => form.reset()), 'Room added.');
});

$('add-queue').addEventListener('submit', (event) => {
  event.preventDefault();
  const form = event.target;
  run(() => api('/queues', {
    method: 'POST',
    body: JSON.stringify({ name: form.name.value, secondsPerPerson: Number(form.seconds.value) }),
  }).then(() => form.reset()), 'Line added.');
});

$('sign-out').addEventListener('click', () => showSignIn());

if (token()) {
  refresh();
} else {
  showSignIn();
}
