// Chat helpers: keep a transcript scrolled to its newest message (knowledge-base assistant, Query), and let a question box send on Enter.
export function scrollToEnd(element) {
  if (element) element.scrollTop = element.scrollHeight;
}

// When an answer is finished: a long answer is shown from its first line (so it can be read from the top), a short one in full at the bottom.
export function showLatest(container) {
  if (!container) return;
  const latest = container.querySelector('[data-testid="exchange"]:last-of-type');
  if (!latest) { scrollToEnd(container); return; }
  const top = latest.getBoundingClientRect().top - container.getBoundingClientRect().top + container.scrollTop - 12;
  if (latest.offsetHeight + 24 > container.clientHeight) container.scrollTop = Math.max(0, top);
  else scrollToEnd(container);
}

// Enter sends, Shift+Enter makes a new line; the box grows with its text up to a limit.
export function bindEnterToSend(textarea, dotNetRef) {
  if (!textarea || textarea.__enterBound) return;
  textarea.__enterBound = true;
  textarea.addEventListener('keydown', (e) => {
    if (e.key === 'Enter' && !e.shiftKey && !e.isComposing) {
      e.preventDefault();
      dotNetRef.invokeMethodAsync('SendFromKeyboard');
    }
  });
  textarea.addEventListener('input', () => grow(textarea));
}

export function resetHeight(textarea) {
  if (textarea) textarea.style.height = '';
}

function grow(textarea) {
  textarea.style.height = 'auto';
  textarea.style.height = Math.min(textarea.scrollHeight, 160) + 'px';
}
