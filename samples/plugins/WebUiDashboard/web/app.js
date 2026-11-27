const nameInput = document.querySelector('#name');
const runButton = document.querySelector('#run');
const status = document.querySelector('#status');
const result = document.querySelector('#result');

function setStatus(text, state = '') {
  status.textContent = text;
  status.className = `status ${state}`.trim();
}

function render(data, message) {
  result.replaceChildren();
  const heading = document.createElement('h2');
  heading.textContent = data.title;
  const greeting = document.createElement('p');
  greeting.textContent = `${data.greeting} ${message ?? ''}`.trim();
  const list = document.createElement('div');
  list.className = 'facts';
  for (const item of data.items) {
    const row = document.createElement('div');
    row.className = 'fact';
    const label = document.createElement('span');
    label.textContent = item.label;
    const value = document.createElement('strong');
    value.textContent = item.value;
    row.append(label, value);
    list.append(row);
  }
  const details = document.createElement('details');
  const summary = document.createElement('summary');
  summary.textContent = '查看 JSON';
  const pre = document.createElement('pre');
  pre.textContent = JSON.stringify(data, null, 2);
  details.append(summary, pre);
  result.append(heading, greeting, list, details);
}

runButton.addEventListener('click', async () => {
  runButton.disabled = true;
  setStatus('正在调用插件进程');
  try {
    const response = await window.hd2.invoke('dashboard', { name: nameInput.value });
    render(response.data, response.message);
    setStatus('桥接正常', 'ready');
  } catch (error) {
    result.replaceChildren();
    const heading = document.createElement('h2');
    heading.textContent = '调用失败';
    const message = document.createElement('p');
    message.className = 'placeholder';
    message.textContent = error instanceof Error ? error.message : String(error);
    result.append(heading, message);
    setStatus('调用失败', 'error');
  } finally {
    runButton.disabled = false;
  }
});
