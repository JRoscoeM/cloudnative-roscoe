// Shrinks the photo in the browser, which also drops location metadata, then
// sends it once. The result is written with textContent, so nothing from the
// response can become markup.
const LONGEST_SIDE = 1024;
const QUALITY = 0.75;

const form = document.getElementById('snapshot-form');
const status = document.getElementById('snapshot-status');
const result = document.getElementById('snapshot-result');
const button = form.querySelector('button');

async function shrink(file) {
  const image = await createImageBitmap(file);
  const scale = Math.min(1, LONGEST_SIDE / Math.max(image.width, image.height));
  const canvas = document.createElement('canvas');
  canvas.width = Math.round(image.width * scale);
  canvas.height = Math.round(image.height * scale);
  canvas.getContext('2d').drawImage(image, 0, 0, canvas.width, canvas.height);
  image.close();
  return new Promise((resolve) => canvas.toBlob(resolve, 'image/jpeg', QUALITY));
}

function showResult(estimate) {
  document.getElementById('result-percent').textContent = `${estimate.fullnessPercent}%`;
  document.getElementById('result-detail').textContent = estimate.counted
    ? `${estimate.room} now reflects your photo. We could see about ${estimate.peopleVisible} people and ${estimate.openSeats} open seats.`
    : `This photo wasn't clear enough to update ${estimate.room}. Try again from further back.`;
  result.hidden = false;
  result.scrollIntoView({ block: 'nearest' });
}

form.addEventListener('submit', async (event) => {
  event.preventDefault();
  result.hidden = true;
  button.disabled = true;
  status.textContent = 'Sending your photo…';

  try {
    const body = new FormData();
    body.append('room', form.room.value);
    body.append('photo', await shrink(form.photo.files[0]), 'room.jpg');

    const response = await fetch('/api/snapshots', { method: 'POST', body });
    if (response.status === 429) {
      status.textContent = 'Lots of photos right now. Please try again in a minute.';
      return;
    }

    const data = await response.json();
    if (!response.ok) {
      status.textContent = data.error;
      return;
    }

    status.textContent = '';
    form.photo.value = '';
    showResult(data);
  } catch {
    status.textContent = "That photo couldn't be sent. Please try again.";
  } finally {
    button.disabled = false;
  }
});
