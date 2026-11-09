// Runs the person detector on this device and reports only the count.
import { ObjectDetector } from '/vendor/mediapipe/vision_bundle.mjs';

const SAMPLE_EVERY_MS = 10_000;
const MEDIAN_OF = 5;
const MIN_SCORE = 0.3;

const $ = (id) => document.getElementById(id);
const form = $('sensor-form');
const target = $('target');
const setupKey = $('setup-key');
const startButton = $('start');
const stopButton = $('stop');
const status = $('sensor-status');
const video = $('video');
const testImage = $('test-image');
const overlay = $('overlay');
const clearRegion = $('clear-region');
const countLabel = $('count');
const lastSent = $('last-sent');

let detector;
let stream;
let timer;
let wakeLock;
let recentCounts = [];
let region = null;
let dragStart = null;
let lastPeople = [];

const tokenKey = (id) => `headroom.token.${id}`;
const regionKey = (id) => `headroom.region.${id}`;

function stored(key) {
  try {
    return localStorage.getItem(key);
  } catch {
    return null;
  }
}

function store(key, value) {
  try {
    if (value === null) {
      localStorage.removeItem(key);
    } else {
      localStorage.setItem(key, value);
    }
  } catch {
    // Without storage the phone just asks for the setup key again.
  }
}

async function loadDetector() {
  try {
    detector = await ObjectDetector.createFromOptions(
      {
        wasmLoaderPath: '/vendor/mediapipe/wasm/vision_wasm_internal.js',
        wasmBinaryPath: '/vendor/mediapipe/wasm/vision_wasm_internal.wasm',
      },
      {
        baseOptions: { modelAssetPath: '/vendor/mediapipe/efficientdet_lite2.tflite', delegate: 'CPU' },
        runningMode: 'IMAGE',
        scoreThreshold: MIN_SCORE,
        categoryAllowlist: ['person'],
      },
    );
    startButton.disabled = false;
    status.textContent = 'Ready.';
  } catch (error) {
    status.textContent = `The detector couldn't load: ${error.message}`;
  }
}

function currentSource() {
  return video.hidden ? testImage : video;
}

function sourceSize(source) {
  return source === video
    ? { width: video.videoWidth, height: video.videoHeight }
    : { width: testImage.naturalWidth, height: testImage.naturalHeight };
}

// Boxes as fractions of the frame, so they survive any display size.
function detectPeople(source) {
  const { width, height } = sourceSize(source);
  return detector.detect(source).detections.map((detection) => ({
    x: detection.boundingBox.originX / width,
    y: detection.boundingBox.originY / height,
    width: detection.boundingBox.width / width,
    height: detection.boundingBox.height / height,
    score: detection.categories[0].score,
  }));
}

function inRegion(person) {
  if (!region) {
    return true;
  }
  const centerX = person.x + person.width / 2;
  const centerY = person.y + person.height / 2;
  return centerX >= region.x && centerX <= region.x + region.width
    && centerY >= region.y && centerY <= region.y + region.height;
}

function draw() {
  const box = overlay.getBoundingClientRect();
  overlay.width = box.width;
  overlay.height = box.height;
  const context = overlay.getContext('2d');
  context.clearRect(0, 0, overlay.width, overlay.height);

  context.lineWidth = 2;
  for (const person of lastPeople) {
    context.strokeStyle = inRegion(person) ? '#3cb76b' : '#9aa0a6';
    context.strokeRect(person.x * box.width, person.y * box.height, person.width * box.width, person.height * box.height);
  }

  if (region) {
    context.lineWidth = 3;
    context.strokeStyle = '#8cabff';
    context.setLineDash([8, 6]);
    context.strokeRect(region.x * box.width, region.y * box.height, region.width * box.width, region.height * box.height);
    context.setLineDash([]);
  }
  clearRegion.hidden = !region;
}

function median(values) {
  const sorted = [...values].sort((a, b) => a - b);
  return sorted[Math.floor(sorted.length / 2)];
}

async function tokenFor(id) {
  const saved = stored(tokenKey(id));
  if (saved) {
    return saved;
  }

  const response = await fetch('/api/sensor/token', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ target: id, setupKey: setupKey.value }),
  });
  const data = await response.json();
  if (!response.ok) {
    throw new Error(data.error);
  }

  store(tokenKey(id), data.token);
  setupKey.value = '';
  return data.token;
}

async function report(token, people, confidence) {
  const response = await fetch('/api/readings', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${token}` },
    body: JSON.stringify({ target: target.value, people, confidence }),
  });

  if (response.status === 401) {
    store(tokenKey(target.value), null);
    throw new Error('This phone is no longer set up for that space. Enter the setup key and start again.');
  }
  if (!response.ok) {
    throw new Error(`The server answered ${response.status}.`);
  }
}

async function sample(token) {
  if (video.readyState >= 2) {
    lastPeople = detectPeople(video);
    const counted = lastPeople.filter(inRegion);
    recentCounts = [...recentCounts, counted.length].slice(-MEDIAN_OF);
    const people = median(recentCounts);
    const confidence = counted.length ? counted.reduce((sum, person) => sum + person.score, 0) / counted.length : 0.5;
    countLabel.textContent = String(people);
    draw();

    try {
      await report(token, people, confidence);
      lastSent.textContent = new Date().toLocaleTimeString();
      status.textContent = 'Counting. Keep this page open.';
    } catch (error) {
      status.textContent = error.message;
      if (!stored(tokenKey(target.value))) {
        stop();
        return;
      }
    }
  }
  timer = setTimeout(() => sample(token), SAMPLE_EVERY_MS);
}

async function keepAwake() {
  try {
    wakeLock = await navigator.wakeLock?.request('screen');
  } catch {
    // Not fatal: the phone's own sleep setting applies instead.
  }
}

async function start() {
  status.textContent = 'Starting…';
  try {
    const token = await tokenFor(target.value);
    stream = await navigator.mediaDevices.getUserMedia({
      audio: false,
      video: { facingMode: { ideal: 'environment' }, width: { ideal: 1920 } },
    });
    video.srcObject = stream;
    testImage.hidden = true;
    video.hidden = false;
    await video.play();

    recentCounts = [];
    startButton.hidden = true;
    stopButton.hidden = false;
    target.disabled = true;
    await keepAwake();
    sample(token);
  } catch (error) {
    status.textContent = error.message;
  }
}

function stop() {
  clearTimeout(timer);
  stream?.getTracks().forEach((track) => track.stop());
  stream = null;
  video.srcObject = null;
  video.hidden = true;
  wakeLock?.release();
  wakeLock = null;
  lastPeople = [];
  startButton.hidden = false;
  stopButton.hidden = true;
  target.disabled = false;
  draw();
}

function pointerPosition(event) {
  const box = overlay.getBoundingClientRect();
  return {
    x: Math.min(1, Math.max(0, (event.clientX - box.left) / box.width)),
    y: Math.min(1, Math.max(0, (event.clientY - box.top) / box.height)),
  };
}

function saveRegion() {
  if (target.value) {
    store(regionKey(target.value), region ? JSON.stringify(region) : null);
  }
}

overlay.addEventListener('pointerdown', (event) => {
  dragStart = pointerPosition(event);
  overlay.setPointerCapture(event.pointerId);
});

overlay.addEventListener('pointermove', (event) => {
  if (!dragStart) {
    return;
  }
  const current = pointerPosition(event);
  region = {
    x: Math.min(dragStart.x, current.x),
    y: Math.min(dragStart.y, current.y),
    width: Math.abs(current.x - dragStart.x),
    height: Math.abs(current.y - dragStart.y),
  };
  draw();
});

overlay.addEventListener('pointerup', () => {
  dragStart = null;
  // A tap makes a tiny box, which would count nobody.
  if (region && (region.width < 0.03 || region.height < 0.03)) {
    region = null;
  }
  saveRegion();
  recentCounts = [];
  draw();
});

clearRegion.addEventListener('click', () => {
  region = null;
  saveRegion();
  recentCounts = [];
  draw();
});

target.addEventListener('change', () => {
  const saved = stored(regionKey(target.value));
  region = saved ? JSON.parse(saved) : null;
  draw();
});

form.addEventListener('submit', (event) => {
  event.preventDefault();
  start();
});

stopButton.addEventListener('click', stop);

// The browser drops the wake lock whenever the page is hidden.
document.addEventListener('visibilitychange', () => {
  if (document.visibilityState === 'visible' && stream) {
    keepAwake();
  }
});

$('test-photo').addEventListener('change', async (event) => {
  const [file] = event.target.files;
  if (!file || !detector) {
    return;
  }

  if (stream) {
    stop();
  }
  testImage.src = URL.createObjectURL(file);
  await testImage.decode();
  URL.revokeObjectURL(testImage.src);
  testImage.hidden = false;

  lastPeople = detectPeople(testImage);
  countLabel.textContent = String(lastPeople.filter(inRegion).length);
  status.textContent = `Test photo: found ${lastPeople.length} ${lastPeople.length === 1 ? 'person' : 'people'}.`;
  draw();
});

window.addEventListener('resize', draw);

loadDetector();
