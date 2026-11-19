-- BaseAdminCore 首次初始化（PostgreSQL 官方镜像仅在数据卷为空时执行本目录脚本）
-- 主库 huluyeye_admin 由 POSTGRES_DB 自动创建；此处幂等补齐测试库。
-- 用 psql \gexec：CREATE DATABASE 不能出现在函数/事务块内（DO $$ 会报
-- "CREATE DATABASE cannot be executed from a function"）。
SELECT 'CREATE DATABASE huluyeye_admin_test'
WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = 'huluyeye_admin_test')\gexec
