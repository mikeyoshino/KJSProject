/**
 * Supabase SQL Migration: Support Tickets System
 * Date: 2026-04-11
 *
 * INSTRUCTIONS FOR RUNNING IN SUPABASE:
 * 1. Go to Supabase Dashboard → SQL Editor
 * 2. Create a new query
 * 3. Copy and paste the entire contents of this file
 * 4. Click "Run" to execute all statements
 * 5. Verify the tables are created: check "Table" view in the Data Editor
 *
 * This migration creates:
 * - support_tickets: Main table for support ticket metadata
 * - ticket_messages: Table for individual messages within a ticket
 * - Foreign key constraint from ticket_messages to support_tickets with CASCADE delete
 * - Indexes for common query patterns (user_id, ticket_id, status, created_at)
 */

-- Create support_tickets table
-- Stores support ticket metadata and status tracking
CREATE TABLE IF NOT EXISTS public.support_tickets (
  id TEXT PRIMARY KEY,
  user_id TEXT NOT NULL,
  user_email TEXT NOT NULL,
  title TEXT NOT NULL,
  category TEXT NOT NULL DEFAULT 'Other' CHECK (category IN ('Payment', 'Download', 'Account', 'Other')),
  status TEXT NOT NULL DEFAULT 'Open' CHECK (status IN ('Open', 'InProgress', 'Resolved')),
  created_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW(),
  updated_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW(),
  last_reply_at TIMESTAMP WITH TIME ZONE
);

-- Create indexes on support_tickets for common query patterns
CREATE INDEX IF NOT EXISTS idx_support_tickets_user_id ON public.support_tickets(user_id);
CREATE INDEX IF NOT EXISTS idx_support_tickets_status ON public.support_tickets(status);
CREATE INDEX IF NOT EXISTS idx_support_tickets_created_at ON public.support_tickets(created_at DESC);

-- Create ticket_messages table
-- Stores individual messages/replies within a support ticket
CREATE TABLE IF NOT EXISTS public.ticket_messages (
  id TEXT PRIMARY KEY,
  ticket_id TEXT NOT NULL REFERENCES public.support_tickets(id) ON DELETE CASCADE,
  sender_id TEXT NOT NULL,
  is_admin BOOLEAN NOT NULL DEFAULT FALSE,
  message TEXT NOT NULL,
  created_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT NOW()
);

-- Create indexes on ticket_messages for common query patterns
CREATE INDEX IF NOT EXISTS idx_ticket_messages_ticket_id ON public.ticket_messages(ticket_id);
CREATE INDEX IF NOT EXISTS idx_ticket_messages_created_at ON public.ticket_messages(created_at DESC);

-- Enable RLS (Row Level Security) for security
-- Tickets are readable by the owner or admin users
ALTER TABLE public.support_tickets ENABLE ROW LEVEL SECURITY;
ALTER TABLE public.ticket_messages ENABLE ROW LEVEL SECURITY;

-- RLS Policy: Users can read their own tickets
CREATE POLICY support_tickets_select_own ON public.support_tickets
  FOR SELECT
  USING (auth.uid()::text = user_id OR (SELECT role FROM auth.users WHERE id = auth.uid()) = 'admin');

-- RLS Policy: Users can read messages from their own tickets
CREATE POLICY ticket_messages_select_own ON public.ticket_messages
  FOR SELECT
  USING (
    ticket_id IN (
      SELECT id FROM public.support_tickets WHERE auth.uid()::text = user_id
    ) OR (SELECT role FROM auth.users WHERE id = auth.uid()) = 'admin'
  );
