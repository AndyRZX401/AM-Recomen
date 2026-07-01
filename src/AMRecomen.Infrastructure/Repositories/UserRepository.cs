using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using AMRecomen.Domain.Entities;
using AMRecomen.Domain.Interfaces;
using AMRecomen.Infrastructure.Data;

namespace AMRecomen.Infrastructure.Repositories;

public class UserRepository : IUserRepository
{
    private readonly AppDbContext _context;

    public UserRepository(AppDbContext context)
    {
        _context = context;
    }

    public async Task<User?> GetByIdAsync(Guid id)
    {
        return await _context.Users.FirstOrDefaultAsync(u => u.Id == id);
    }

    public async Task<User?> GetByUsernameAsync(string username)
    {
        var normalized = username.ToLower().Trim();
        return await _context.Users.FirstOrDefaultAsync(u => u.Username.ToLower() == normalized);
    }

    public async Task<User?> GetByEmailAsync(string email)
    {
        var normalized = email.ToLower().Trim();
        return await _context.Users.FirstOrDefaultAsync(u => u.Email.ToLower() == normalized);
    }

    public async Task AddAsync(User user)
    {
        await _context.Users.AddAsync(user);
    }

    public async Task SaveChangesAsync()
    {
        await _context.SaveChangesAsync();
    }
}
