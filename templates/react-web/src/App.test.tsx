import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import App from './App';

describe('App', () => {
  it('asks for a name at first', () => {
    render(<App />);

    expect(screen.getByRole('status')).toHaveTextContent('Type your name');
  });

  it('greets the person as they type', async () => {
    render(<App />);

    await userEvent.type(screen.getByLabelText('Your name'), 'Ada');

    expect(screen.getByRole('status')).toHaveTextContent('Hello, Ada!');
  });

  it('explains what is allowed when the name is not usable', async () => {
    render(<App />);

    await userEvent.type(screen.getByLabelText('Your name'), '<b>');

    expect(screen.getByRole('status')).toHaveTextContent('Use 1 to 50 letters');
  });
});
