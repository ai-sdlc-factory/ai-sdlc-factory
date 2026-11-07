import { useState } from 'react';
import { greetingFor, MAX_NAME_LENGTH } from './greeting';

export default function App() {
  const [name, setName] = useState('');
  const greeting = greetingFor(name);

  return (
    <main className="page">
      <h1>Greetings</h1>
      <label htmlFor="name">Your name</label>
      <input
        id="name"
        value={name}
        maxLength={MAX_NAME_LENGTH + 1}
        onChange={(event) => setName(event.target.value)}
        aria-describedby="result"
      />
      <p id="result" role="status">
        {name.trim() === ''
          ? 'Type your name to be greeted.'
          : greeting ?? `Use 1 to ${MAX_NAME_LENGTH} letters, digits, spaces, dots or dashes.`}
      </p>
    </main>
  );
}
