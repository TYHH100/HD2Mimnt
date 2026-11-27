let count = 0;
const output = document.querySelector('#count');
const button = document.querySelector('#increment');

button.addEventListener('click', () => {
  count += 1;
  output.value = String(count);
});
