-- Admin CRM migrations
-- Run these in the Supabase SQL editor

-- 1. Add email column to subscriptions table
alter table subscriptions add column if not exists email text not null default '';

-- 2. Create banned_users table
create table if not exists banned_users (
  user_id   text primary key,
  email     text not null,
  reason    text,
  banned_at timestamptz default now(),
  banned_by text
);
