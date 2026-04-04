CREATE TABLE IF NOT EXISTS public.subscriptions (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id TEXT NOT NULL,
    plan TEXT NOT NULL,
    amount_usd NUMERIC NOT NULL,
    amount_btc NUMERIC,
    btc_address TEXT,
    txid TEXT,
    status TEXT NOT NULL DEFAULT 'pending',
    created_at TIMESTAMPTZ DEFAULT NOW(),
    paid_at TIMESTAMPTZ,
    expires_at TIMESTAMPTZ
);

-- Index for querying by user
CREATE INDEX IF NOT EXISTS subscriptions_user_id_idx ON public.subscriptions(user_id);

-- Index for searching by btc address
CREATE INDEX IF NOT EXISTS subscriptions_btc_address_idx ON public.subscriptions(btc_address);
