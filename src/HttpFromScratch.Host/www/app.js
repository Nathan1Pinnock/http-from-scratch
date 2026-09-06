// Fetches /hello over the same keep-alive connection the page came in on and
// shows what the server said, so the page proves the server works end to end.
fetch("/hello")
  .then((response) => response.text().then((text) => ({ response, text })))
  .then(({ response, text }) => {
    document.getElementById("proof").textContent =
      `fetch("/hello") got ${response.status} from ${response.headers.get("server")}: ${text.trim()}`;
  })
  .catch((error) => {
    document.getElementById("proof").textContent = `fetch("/hello") failed: ${error}`;
  });
