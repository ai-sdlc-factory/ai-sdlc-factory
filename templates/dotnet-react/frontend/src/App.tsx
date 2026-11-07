import { FormEvent, useState } from 'react';
import { fetchGreeting } from './api';

export default function App() {
  const [name, setName] = useState('');
  const [status, setStatus] = useState<'idle' | 'loading' | 'done' | 'failed'>('idle');
  const [text, setText] = useState('Type your name and press Greet.');

  async function submit(event: FormEvent) {
    event.preventDefault();
    setStatus('loading');
    setText('Asking the server...');
    const result = await fetchGreeting(name);
    setStatus(result.ok ? 'done' : 'failed');
    setText(result.ok ? result.message : result.error);
  }

  return (
    <main className="page">
      <h1>Greetings</h1>
      <form onSubmit={submit} className="form">
        <label htmlFor="name">Your name</label>
        <input id="name" value={name} onChange={(event) => setName(event.target.value)} aria-describedby="result" />
        <button type="submit" disabled={status === 'loading' || name.trim() === ''}>Greet</button>
      </form>
      <p id="result" role="status" className={status === 'failed' ? 'error' : undefined}>{text}</p>
    </main>
  );
}
