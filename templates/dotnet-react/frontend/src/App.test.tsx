import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import App from './App';

describe('App', () => {
  it('cannot be submitted without a name', () => {
    render(<App />);

    expect(screen.getByRole('button', { name: 'Greet' })).toBeDisabled();
  });

  it('shows the greeting from the server', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: true, status: 200, json: async () => ({ message: 'Hello, Ada!' }) }));
    render(<App />);

    await userEvent.type(screen.getByLabelText('Your name'), 'Ada');
    await userEvent.click(screen.getByRole('button', { name: 'Greet' }));

    expect(await screen.findByText('Hello, Ada!')).toBeInTheDocument();
  });

  it('shows the error when the server refuses the name', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: false, status: 400, json: async () => ({ error: 'Not usable.' }) }));
    render(<App />);

    await userEvent.type(screen.getByLabelText('Your name'), 'x');
    await userEvent.click(screen.getByRole('button', { name: 'Greet' }));

    expect(await screen.findByText('Not usable.')).toBeInTheDocument();
  });
});
