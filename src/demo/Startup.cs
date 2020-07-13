﻿using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using EntityGraphQL.Schema;
using Microsoft.Extensions.Hosting;

namespace demo
{
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {            
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddDbContext<DemoContext>(opt => opt.UseSqlite("Filename=demo.db"));

            services.AddSingleton<AgeService>();

            // add schema provider so we don't need to create it everytime
            services.AddSingleton(GraphQLSchema.MakeSchema());            
            services.AddControllers()
                .AddNewtonsoftJson();

        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env, DemoContext db)
        {
            CreateData(db);

            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            app.UseHttpsRedirection();

            app.UseRouting();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers();
            });
        }

        private static void CreateData(DemoContext db)
        {
            db.Database.EnsureDeleted();
            db.Database.EnsureCreated();

            // add test data
            var shawshank = new Movie
            {
                Name = "The Shawshank Redemption",
                Genre = Genre.Drama,
                Released = new DateTime(1994, 10, 14),
                Rating = 9.2,
                Director = new Person
                {
                    FirstName = "Frank",
                    LastName = "Darabont",
                    Dob = new DateTime(1959, 1, 28),
                }
            };
            shawshank.Actors = new List<Actor> {
                new Actor {
                    Person = new Person {
                        Dob = new DateTime(1958, 10, 16),
                        FirstName = "Tim",
                        LastName = "Robbins",
                    },
                },
            };
            db.Movies.Add(shawshank);
            var francis = new Person
            {
                Dob = new DateTime(1939, 4, 7),
                FirstName = "Francis",
                LastName = "Coppola",
            };
            var godfather = new Movie
            {
                Name = "The Godfather",
                Genre = Genre.Drama,
                Released = new DateTime(1972, 3, 24),
                Rating = 9.2,
                Director = francis,
            };
            godfather.Actors = new List<Actor> {
                new Actor {
                    Person = new Person {
                        Dob = new DateTime(1924, 4, 3),
                        Died = new DateTime(2004, 7, 1),
                        FirstName = "Marlon",
                        LastName = "Brando",
                    },
                },
                new Actor {
                    Person = new Person {
                        Dob = new DateTime(1940, 4, 25),
                        FirstName = "Al",
                        LastName = "Pacino",
                    },
                },
            };
            godfather.Writers = new List<Writer> {
                new Writer {
                    Person = francis,
                }
            };

            db.Movies.Add(godfather);

            db.SaveChanges();
        }
    }
}
